# ICEHOTT Phase 7D Recovery Tools

This directory contains the executable backup, restore, retention, artifact-recovery and disaster-recovery drill tooling for Phase 7D.

Targets:

- RPO <= 3600 seconds
- RTO <= 7200 seconds
- retention: hourly 48h, daily 14d, weekly 8 weeks

PostgreSQL remains authoritative for metadata and authorization. Object storage contains bytes only.

## Install

Use the recovery-only pinned dependencies; they are intentionally separate from the AI runtime.

```bash
python3 -m pip install -r scripts/recovery/requirements.txt
```

## Tools

| Tool | Purpose |
|---|---|
| `backup-postgres.sh` | `pg_dump -Fc`, structural verification, dump SHA-256 and safe manifest publication |
| `verify-postgres-backup.py` | Verify manifest, size, SHA-256, age and `pg_restore --list` |
| `restore-postgres.sh` | Explicit-confirmation restore to an existing safe target |
| `prune-backups.py` | 48h / 14d / 8w UTC retention; dry-run by default |
| `backup-artifacts.py` | Stream final artifact objects to an immutable backup set |
| `restore-artifacts.py` | Restore to a pre-created target namespace without blind overwrite |
| `reconcile-restore.py` | Read-only DB/object integrity reconciliation |
| `restore-drill.sh` | Isolated PostgreSQL + S3-compatible end-to-end restore drill |
| `postgres_ops.py` | Shared PostgreSQL backup/restore implementation |
| `recovery_common.py` | Shared validation, path, manifest, DSN and S3 helpers |

## PostgreSQL backup

Supply the source DSN through the secret environment only:

```bash
export ICEHOTT_BACKUP_DATABASE_URL="<dsn-from-secret-store>"
bash scripts/recovery/backup-postgres.sh \
  --backup-dir /var/backups/icehott \
  --release-sha <40-char-git-sha>
```

The raw DSN is parsed into libpq `PG*` environment variables and removed before PostgreSQL child processes execute. It is never written to the manifest or placed on the child command line.

A successful backup publishes:

- `icehott-<32hex>.dump`
- `icehott-<32hex>.manifest.json`

The manifest schema is version 1 and contains only safe metadata plus the dump's exact size and SHA-256.

## PostgreSQL verify and restore

```bash
python3 scripts/recovery/verify-postgres-backup.py \
  /var/backups/icehott/icehott-<id>.manifest.json \
  --max-age-seconds 3600
```

Restore requires an existing target DB, an explicit confirmation, and a target name containing `restore`, `drill` or `test` unless the documented dangerous production override is deliberately enabled:

```bash
export ICEHOTT_RESTORE_DATABASE_URL="<isolated-target-dsn-from-secret-store>"
export ICEHOTT_RESTORE_CONFIRM="RESTORE"
bash scripts/recovery/restore-postgres.sh \
  --manifest /var/backups/icehott/icehott-<id>.manifest.json
```

The restore uses `pg_restore --clean --if-exists --no-owner --no-privileges --exit-on-error`. It does not create or drop the database itself.

## Retention

```bash
# dry-run
python3 scripts/recovery/prune-backups.py --backup-dir /var/backups/icehott

# apply
python3 scripts/recovery/prune-backups.py --backup-dir /var/backups/icehott --apply
```

Only complete known backup pairs are eligible for deletion. Unknown/incomplete files are left untouched.

## Artifact backup / restore

Use role-specific secret pairs when explicit credentials are required:

- `ICEHOTT_SOURCE_S3_ACCESS_KEY_ID` / `ICEHOTT_SOURCE_S3_SECRET_ACCESS_KEY`
- `ICEHOTT_BACKUP_S3_ACCESS_KEY_ID` / `ICEHOTT_BACKUP_S3_SECRET_ACCESS_KEY`
- `ICEHOTT_TARGET_S3_ACCESS_KEY_ID` / `ICEHOTT_TARGET_S3_SECRET_ACCESS_KEY`

The normal AWS credential chain is used when a role-specific pair is omitted.

Artifact backup covers canonical final keys only:

`objects/{workspace:N}/{artifact:N}.bin`

Staging objects are not authoritative backups. Bytes are streamed and SHA-256 verified. No public ACL or presigned URL is created.

## Reconciliation

The database DSN is provided only through:

`ICEHOTT_RECONCILE_DATABASE_URL`

The reconciliation tool is read-only. In strict mode it fails on missing/corrupt Ready artifacts, malformed final keys, unknown statuses and unexpected orphan final objects.

## Restore drill

```bash
export GIT_SHA=<40-char-git-sha>
export RECOVERY_EVIDENCE_OUT=/tmp/icehott-recovery-evidence.json
bash scripts/recovery/restore-drill.sh
```

The drill uses fresh source/target PostgreSQL instances and independent source/target S3-compatible namespaces. It applies real migrations, seeds real tenant/workflow/artifact relationships, performs both backup/restore paths, runs strict reconciliation, verifies restored data, and emits safe machine-readable RPO/RTO evidence.

The CI drill proves procedure correctness. It does **not** prove production-provider throughput or a production-region RTO.

## Tests

```bash
cd scripts/recovery
python3 -m pytest -q
```

On Windows, Linux shell/symlink-specific assertions are skipped locally and are mandatory in the Ubuntu Recovery CI job.

## Production requirements

- managed-provider PostgreSQL backups/PITR enabled;
- logical backup at least hourly;
- encrypted backup destination outside the live DB volume;
- separate bucket/account/project recommended for artifact backup/replication;
- hourly 48h, daily 14d, weekly 8w retention;
- no backup considered valid until verification passes;
- monthly restore drill initially, plus a live-provider drill before launch and after material DB/storage changes;
- production restores require incident approval and the explicit dangerous override;
- application startup never performs restore.

See `docs/PHASE-7D-BACKUP-RESTORE-DR.md` and `docs/runbooks/PHASE-7D-DR-RUNBOOK.md` for the full production contract.
