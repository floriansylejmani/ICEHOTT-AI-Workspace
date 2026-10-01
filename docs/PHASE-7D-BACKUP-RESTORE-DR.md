# Phase 7D — Backup, Restore & Disaster Recovery

Status: implemented on `phase-7d-backup-restore-dr`; pending GitHub recovery drill, PR review, merge, and live-provider verification.

## Objectives

Phase 7D adds a verified recovery path for the durable ICEHOTT state:

- PostgreSQL/pgvector remains authoritative for metadata, tenant authorization, workflow state, quotas, audit, and artifact lifecycle.
- S3-compatible object storage contains artifact bytes only.
- Redis is not a durable recovery authority.
- Recovery point objective (RPO): **<= 1 hour**.
- Recovery time objective (RTO): **<= 2 hours**.
- Retention: **48 hourly**, **14 daily**, **8 weekly** backup points.
- A backup is not considered valid until its integrity is verified.

Phase 7D does not auto-restore production, auto-create production buckets, down-migrate EF schemas, or grant authorization from object existence.

## Recovery tooling

All operational tooling is under `scripts/recovery/`.

| Tool | Purpose |
| --- | --- |
| `backup-postgres.sh` / `postgres_ops.py backup` | Produce an atomic PostgreSQL logical backup and manifest |
| `verify-postgres-backup.py` | Validate manifest, age, size, SHA-256, and pg_restore structure |
| `restore-postgres.sh` / `postgres_ops.py restore` | Restore a verified backup into an existing, explicitly confirmed target |
| `prune-backups.py` | Apply 48h / 14d / 8w retention; dry-run by default |
| `backup-artifacts.py` | Stream final artifact bytes into an immutable backup set |
| `restore-artifacts.py` | Restore and re-verify artifact bytes without blind overwrite |
| `reconcile-restore.py` | Read-only comparison of restored DB artifact metadata and object bytes |
| `restore-drill.sh` | End-to-end isolated PostgreSQL + S3-compatible restore drill |

Recovery-only Python dependencies are pinned in `scripts/recovery/requirements.txt` and are not added to the FastAPI runtime.

## PostgreSQL backup format

PostgreSQL backups use `pg_dump --format=custom --no-owner --no-privileges`.

The dump is written into a temporary directory first. Before publication:

1. `pg_dump` must exit successfully.
2. `pg_restore --list` must validate the archive.
3. The dump must be non-empty.
4. SHA-256 and byte size are calculated.
5. The manifest is generated.
6. Dump and manifest are atomically moved into the configured backup directory.

A failed backup must not leave a valid-looking published manifest/dump pair.

### PostgreSQL manifest schema v1

The manifest contains only safe operational metadata:

- `schema_version`
- `backup_id`
- `created_at_utc`
- `dump_filename`
- `bytes`
- `sha256`
- `format`
- `pg_dump_version`
- optional `release_git_sha`

It must never contain host, port, username, password, connection URL, access key, secret key, token, or credential material.

## PostgreSQL connection safety

Backup uses `ICEHOTT_BACKUP_DATABASE_URL`; restore uses `ICEHOTT_RESTORE_DATABASE_URL`.

The URL is parsed by `recovery_common.parse_postgres_url`, including percent-decoded credentials and IPv6 hosts. Child PostgreSQL processes receive only libpq `PG*` environment variables; the raw connection URL is removed before child process execution.

The tooling does not print the connection URL or password.

## PostgreSQL restore safety

Restore is deliberately fail-closed.

Requirements:

- `ICEHOTT_RESTORE_CONFIRM=RESTORE`
- target URL is explicitly supplied
- target database already exists and is reachable
- manifest and dump verify before the target is touched
- safe targets contain an isolation marker such as `restore`, `drill`, or `test`
- non-isolated targets require the deliberate `ICEHOTT_ALLOW_DANGEROUS_RESTORE=true` override

Restore uses:

`pg_restore --clean --if-exists --no-owner --no-privileges --exit-on-error`

The script never silently creates or drops the production database itself.

## Retention

`prune-backups.py` implements deterministic UTC retention:

- hourly backup points: 48 hours
- daily backup points: 14 days
- weekly backup points: 8 weeks

A backup selected by any tier is retained.

Pruning is dry-run by default. Deletion requires `--apply`.

Unknown files, incomplete backup sets, traversal targets, and symlink escapes are not silently deleted.

## Artifact backup

Only canonical final keys are backed up:

`objects/{workspace:N}/{artifact:N}.bin`

The application storage prefix is remote namespace configuration only; it does not change the logical key stored in PostgreSQL.

Artifact backup:

1. lists only the final `objects/` namespace
2. rejects malformed logical keys
3. streams object bytes
4. calculates exact size and SHA-256
5. writes the byte stream to an immutable backup-set key using conditional creation
6. reads the backup copy again and verifies size and SHA-256
7. publishes the manifest checksum and manifest only after all objects succeed

Staging objects are not authoritative backup data.

No public ACL or presigned public URL is created.

## Artifact backup manifest

Artifact manifests use `schema_version=1` and contain:

- `backup_set_id`
- `created_at_utc`
- optional `source_release_sha`
- `object_count`
- `total_bytes`
- objects with:
  - `logical_key`
  - `bytes`
  - `sha256`

A SHA-256 sidecar protects the locally published manifest. Provider credentials, endpoints, bucket names, and artifact contents are intentionally excluded from the manifest.

## Artifact restore

Restore requires explicit backup and target bucket configuration.

By default the target namespace must be empty.

For each object:

1. the logical key is validated
2. backup bytes are streamed and verified against manifest size/SHA
3. target creation uses conditional non-overwrite
4. if a concurrent writer already created the target, its bytes are verified instead of overwritten
5. restored target bytes are read again and verified

An existing mismatched object fails closed.

Production bucket creation is an operator/provider responsibility; application recovery tooling does not auto-create production buckets.

## Read-only reconciliation

`reconcile-restore.py` compares restored PostgreSQL artifact metadata with target object storage.

For each `Ready` artifact:

- `StorageKey` must be canonical
- workspace/artifact identity in the key must match database IDs
- the final object must exist
- actual bytes must equal `SizeBytes`
- actual SHA-256 must equal `Sha256`

The reconciler also reports:

- Pending artifacts
- Failed artifacts
- logically Deleted artifacts awaiting physical cleanup
- Deleted artifacts whose storage cleanup completed
- malformed final keys
- orphan final objects

Strict mode fails on missing/corrupt/mismatched Ready objects, malformed final objects, unknown statuses, and unexpected orphan final objects.

Reconciliation is read-only. It does not repair rows, authorize objects, or delete storage.

## Deleted and staging semantics

For logically Deleted artifacts, `StorageDeletedAtUtc` distinguishes pending physical cleanup from completed physical cleanup.

Pending staging objects are not treated as restored final data and do not authorize access.

PostgreSQL remains the authorization authority after restore.

## Isolated restore drill

`restore-drill.sh` exercises the complete recovery path using isolated ephemeral services:

- source PostgreSQL/pgvector
- target PostgreSQL/pgvector
- source/backup S3-compatible MinIO
- separate target S3-compatible MinIO

The drill:

1. starts isolated services
2. applies the real ICEHOTT EF schema to source PostgreSQL
3. seeds a valid user, workspace, workspace membership, workflow definition/version/run, and Ready artifact
4. uploads real artifact bytes
5. records source recovery time
6. creates and verifies PostgreSQL backup
7. creates and verifies artifact backup
8. restores PostgreSQL into a fresh target
9. restores artifact bytes into a separate target object service
10. runs strict read-only reconciliation
11. verifies representative user/workspace/workflow/artifact state
12. writes machine-readable recovery evidence
13. asserts RPO <= 3600 seconds and RTO <= 7200 seconds

The CI drill proves procedure correctness on local ephemeral infrastructure. It does not prove production-provider throughput or regional recovery time.

## Recovery evidence

Recovery evidence includes only safe fields:

- schema version
- source release SHA
- source/backup/restore timestamps
- PostgreSQL dump SHA-256
- artifact manifest SHA-256
- artifact count and total bytes
- reconciliation counts
- measured RPO seconds
- measured RTO seconds
- target RPO/RTO values
- RPO/RTO/reconciliation/overall pass flags

Evidence must not contain database URLs, usernames, passwords, access keys, secret keys, presigned URLs, artifact contents, JWTs, or prompts.

## CI recovery gate

`.github/workflows/recovery.yml` runs independently of external SaaS accounts.

It contains:

- recovery workflow YAML validation
- pinned recovery dependency installation
- shell syntax checks
- Python compile checks
- complete recovery unit test suite
- EF migration bundle build
- full PostgreSQL + MinIO restore drill
- machine-readable evidence validation
- recovery evidence artifact upload

The recovery workflow runs on pull requests and applicable pushes. Missing drill evidence fails the workflow.

## Production backup contract

Before production launch:

- provider-native PostgreSQL automated backups are mandatory
- logical `pg_dump` runs at least hourly
- backup destination is outside the live DB volume
- encryption at rest is required
- artifact backup/replication should use a separate account/project/bucket where practical
- retention is 48 hourly / 14 daily / 8 weekly points
- backup existence and integrity are checked automatically
- restore drill runs at least monthly initially
- a live-provider recovery drill is required before launch and after material database/storage changes
- provider PITR/snapshots are defense in depth, not a replacement for logical restore testing

## Release rollback

Application rollback and schema recovery are separate operations.

Application rollback uses a previously verified immutable Git SHA/image.

EF down-migrations are never run automatically in production.

If a bad release is incompatible with the current schema:

1. stop/promote no further writes as incident procedure requires
2. restore a verified database backup into an isolated environment
3. verify application compatibility and artifact reconciliation
4. require incident approval before any destructive production restore
5. deploy the known-good application SHA
6. run health/readiness/workflow/artifact smoke checks
7. record incident/release evidence

Railway/Vercel live rollback commands remain provider-verified/manual until their exact rollback contracts are tested.

## DR runbook

The operational incident procedures are documented in:

`docs/runbooks/PHASE-7D-DR-RUNBOOK.md`

It covers API, AI, worker, PostgreSQL, object storage, release/migration, credential, regional/provider, and backup-integrity incidents.

## Security properties

Phase 7D is designed so that:

- raw connection URLs are not persisted or printed
- secrets are excluded from manifests and evidence
- backup paths reject traversal and symlink escape
- restore targets are isolated by default
- no production database is silently created/dropped
- object restore never makes data public
- mismatched objects are not blindly overwritten
- restored artifact bytes are SHA-256 verified
- reconciliation is read-only
- object existence never grants authorization
- no restore runs during API startup
- recovery output contains no artifact bytes
- production backup encryption is an explicit operator requirement

## Verification status

Local Windows verification can prove recovery unit/static checks, but Linux-specific shell/symlink tests and the Docker restore drill are authoritative in GitHub Recovery CI.

Live-provider verification remains required for:

- provider-native PostgreSQL backups/PITR
- production backup scheduler
- production backup destination and encryption
- production S3 versioning/replication
- production IAM/credential scope
- production network throughput
- real production RTO
- regional/provider recovery
- Railway/Vercel rollback procedures
- live paging/escalation

## Phase boundary

Phase 7D ends with verified backup/restore/DR procedure and recovery evidence.

Phase 7E remains separate and covers performance testing and the final production release gate.
