# Phase 7B — Managed Data & S3-Compatible Object Storage

**Status:** Implemented on `phase-7b-managed-data-object-storage`, pending CI/PR review

**Baseline:** `main` at `b3039d0102266a34e3cb560a303e93d5d8292bf2`

**Parent architecture:** [PHASE-7-PRODUCTION-DEPLOYMENT-SRE.md](PHASE-7-PRODUCTION-DEPLOYMENT-SRE.md)

## Objective

Phase 7B moves artifact bytes from a development-only local filesystem assumption to a provider-neutral storage contract with a production S3-compatible implementation.

PostgreSQL remains authoritative for artifact metadata, workspace authorization, quotas, workflow/run binding, lifecycle state, idempotency and audit. The object store owns bytes only.

This phase does not deploy a live object-storage account. Live provider setup and verification remain an operational gate.

## Data authority

The durable authority remains PostgreSQL/pgvector.

Redis is not a runtime dependency of the current backend. Phase 7B therefore does not add Redis merely to satisfy infrastructure topology. If Redis is introduced later, it remains optional acceleration/coordination and cannot become the source of truth for workflows, artifacts, approvals, schedules or tool execution.

No Phase 7B database schema migration is required.

## Artifact storage providers

The application supports two configured providers:

- `Local`: development/test filesystem provider.
- `S3`: production S3-compatible provider.

The application/domain contract remains `IArtifactStore`; no provider-specific type leaks into the application layer.

Hosted staging/production rejects Local storage unless the explicit temporary safety override is enabled.

Unknown providers fail startup.

## Configuration

`ArtifactStorage:Provider` selects `Local` or `S3`.

S3 configuration is read from `ObjectStorage`:

- `Endpoint`
- `Bucket`
- `Region`
- `AccessKeyId`
- `SecretAccessKey`
- `ForcePathStyle`
- `Prefix`

Production settings contain only safe defaults:

- region defaults to `us-east-1`;
- path-style support is enabled by default for broad S3 compatibility;
- prefix defaults to empty;
- endpoint, bucket and credentials are not committed.

Hosted validation requires an explicit HTTPS, non-loopback endpoint with no embedded credentials. Access key and secret values are never included in validation messages.

Local development may use HTTP endpoints such as MinIO; the hosted validator is intentionally stricter.

## Logical keys

Persisted logical keys remain compatible with Phase 5D:

```text
staging/{workspace:N}/{artifact:N}.stage
objects/{workspace:N}/{artifact:N}.bin
```

An optional object-store prefix is applied only to remote keys.

For example, a configured prefix `icehott/production` maps:

```text
objects/<workspace>/<artifact>.bin
```

to:

```text
icehott/production/objects/<workspace>/<artifact>.bin
```

The prefix is not written into PostgreSQL and cannot change workspace/artifact identity.

Logical keys reject malformed UUIDs, backslashes, absolute paths, traversal segments, unexpected roots/extensions and non-canonical identifiers.

## Upload lifecycle

The S3 provider preserves the existing durable lifecycle:

1. The API validates membership, workflow binding and file policy.
2. The store streams the source into a bounded temporary file while computing SHA-256 and exact size.
3. The staging object is uploaded with conditional creation (`If-None-Match: *`).
4. PostgreSQL persists the Pending artifact and authoritative metadata.
5. Commit verifies staging bytes by streaming them and recomputing SHA-256.
6. Staging is copied to the final key using conditional destination creation.
7. A concurrent destination winner is accepted only after exact size/SHA verification.
8. The final object is streamed and verified again.
9. Staging is deleted.
10. PostgreSQL transitions the artifact to Ready and emits the existing append-only audit event.

A pre-existing final object is never blindly overwritten.

## Integrity

S3 ETags are not treated as content hashes.

`GetInfoAsync` streams the actual object and computes SHA-256. Size and SHA must match PostgreSQL metadata before content is returned or a Pending artifact is recovered.

An integrity mismatch fails closed with `ArtifactStoreIntegrityException`.

Provider/network failures are translated to `ArtifactStoreUnavailableException`; SDK/provider exception details do not become API error messages.

Cancellation remains cancellation and is not translated into a storage failure.

## Recovery

The existing artifact maintenance service remains the recovery authority.

For a Pending artifact:

- valid staging bytes can be committed and recovered to Ready;
- valid final bytes can be recognized;
- missing or corrupt durable bytes fail the artifact;
- transient object-store unavailability defers recovery.

Failed staging cleanup and physical deletion are retried by maintenance.

The application also deletes stale staging objects under only the configured staging prefix. It validates every listed logical key before deleting it and respects a maximum batch size.

Provider lifecycle rules for stale staging objects are recommended as defense in depth; they do not replace application recovery.

## Reads and deletes

Reads are server-mediated.

The browser never receives object-storage credentials or a public/presigned URL in Phase 7B.

`OpenReadAsync` returns a stream that owns and disposes the provider response.

Deletes are idempotent. PostgreSQL authorization and lifecycle checks occur before physical deletion.

## Production bucket contract

The production/staging bucket must be created by infrastructure/operator setup, not by application startup.

Required controls:

- private bucket;
- public access disabled;
- least-privilege credentials restricted to the configured bucket/prefix;
- TLS endpoint;
- provider-side server encryption where available;
- independent staging and production credentials/prefixes or buckets;
- lifecycle rule for stale `staging/` objects as defense in depth;
- backup/versioning policy documented by the selected provider;
- no direct browser CORS requirement because browser clients do not access the bucket directly.

The production application does not auto-create buckets.

## CI integration proof

CI starts a pinned S3-compatible MinIO community image for the backend job.

The image is pinned to a release and amd64 digest rather than `latest`.

The CI runner sets test-only credentials and the `ICEHOTT_S3_TEST_*` variables. The integration suite fails CI if those variables are absent.

The real S3-compatible suite covers:

- endpoint/bucket connectivity;
- stage -> commit -> info -> read -> delete;
- exact size and SHA-256;
- empty content rejection;
- exact max-size boundary and over-limit cleanup;
- large streamed content;
- conditional staging non-overwrite;
- idempotent commit;
- concurrent commit winner verification;
- correct pre-existing final object handling;
- corrupt pre-existing final object rejection;
- cross-workspace/artifact key rejection;
- cleanup age and max-items;
- final-object protection during cleanup;
- prefix isolation and prefix-boundary cleanup;
- missing object semantics.

CI parses the test result and requires the complete S3 integration class to execute successfully; a missing environment cannot silently count as proof.

## Security invariants

Phase 7B preserves these requirements:

1. PostgreSQL remains server-authoritative for tenant and artifact identity.
2. Object keys cannot escape the configured prefix or cross workspace/artifact identity.
3. The S3 implementation never sets a public ACL.
4. No presigned/public object URL is generated.
5. Staging/final creation uses conditional non-overwrite behavior.
6. An unrelated pre-existing final object cannot be overwritten.
7. Integrity mismatch fails closed.
8. Provider credentials are configuration secrets and are not logged, audited or returned.
9. Hosted object endpoints must be explicit HTTPS and non-loopback.
10. Local storage remains prohibited in hosted mode unless the deliberate override is enabled.
11. Production bucket creation is never automatic.
12. Maintenance cleanup can delete only valid stale staging keys inside its configured prefix.

The object-storage endpoint is operator-controlled infrastructure configuration. It is not derived from user input.

## Operator reference

### Environment variable names

`ArtifactStorage__Provider` (`Local` | `S3`), `ObjectStorage__Endpoint`, `ObjectStorage__Bucket`, `ObjectStorage__Region` (default `us-east-1`), `ObjectStorage__AccessKeyId`, `ObjectStorage__SecretAccessKey`, `ObjectStorage__ForcePathStyle` (default `true`), `ObjectStorage__Prefix` (default empty). Only the S3 provider reads `ObjectStorage__*`; it does not need `ArtifactStorage__RootPath`. The access key and secret key are secrets and must come from the platform secret store.

Prefix rules: empty is allowed; otherwise no leading or trailing `/`, no `//`, no `\`, no `.` or `..` segment, no control characters, and segments are limited to letters, digits, `-`, `_` and `.`. Bucket rules: 3–63 characters of lowercase letters, digits, dots and hyphens; no leading/trailing dot or hyphen, no `..`, not an IP address.

### Provider unavailable behavior

| Operation | Provider unavailable (connectivity, timeout, 5xx, throttling, auth failure) |
| --- | --- |
| Stage | `artifact_storage_unavailable`; nothing persisted |
| Commit | `artifact_storage_unavailable`; artifact stays `Pending` and is recovered later |
| Read | `artifact_storage_unavailable` |
| Physical delete | Deletion stays durably pending in PostgreSQL; maintenance retries |
| Staging cleanup | Deferred to the next maintenance pass |
| Maintenance recovery | Deferred; the artifact is never failed because of an outage |

Integrity mismatches are not treated as transient and fail closed. A missing staged object for a Pending artifact follows the existing failed-recovery semantics.

### Running the MinIO integration tests locally

```bash
docker run -d --name icehott-minio -p 9000:9000 \
  -e MINIO_ROOT_USER=icehott-test -e MINIO_ROOT_PASSWORD=icehott-test-secret \
  pgsty/minio:RELEASE.2026-08-04T00-00-00Z-amd64@sha256:2b36182f3479c58b5cba920f20479738ee85ce218de0596a244f9a1368268db9 \
  server /data
export ICEHOTT_S3_TEST_ENDPOINT=http://localhost:9000
export ICEHOTT_S3_TEST_BUCKET=icehott-test-artifacts
export ICEHOTT_S3_TEST_ACCESS_KEY=icehott-test
export ICEHOTT_S3_TEST_SECRET_KEY=icehott-test-secret
export ICEHOTT_S3_TEST_REGION=us-east-1
dotnet test backend/ICEHOTT.sln --filter "FullyQualifiedName~S3ArtifactStoreIntegrationTests"
```

Local runs skip the suite when the variables are absent. When `CI=true` a missing variable fails the tests instead of skipping them, and CI creates the bucket explicitly (the tests only auto-create the bucket outside CI). Every test works in its own `phase7b-tests/{guid}` prefix and deletes it afterwards.

### IAM contract

Least-privilege credentials dedicated to this service: `s3:GetObject`, `s3:PutObject` and `s3:DeleteObject` on `arn:aws:s3:::{bucket}/{prefix/}*`, and `s3:ListBucket` on the bucket conditioned on the prefix. No bucket-management, ACL, policy or cross-bucket actions. Conditional writes (`If-None-Match`) must be permitted. Recommended lifecycle rule: expire `{prefix/}staging/` objects after several days; application maintenance normally removes them within `ArtifactStorage:StagingRetentionMinutes`.

Versioning policy: optional. If enabled, physical deletes create delete markers, so add a noncurrent-version expiration rule to honor deletion. If disabled, deletes are immediate and irreversible, so backup/restore of object data belongs to Phase 7D.

## Package

The infrastructure project pins `AWSSDK.S3` **4.0.103.1** and uses its native conditional `IfNoneMatch` support. Note that this SDK returns a `null` (not empty) `S3Objects` collection for an empty listing; the store and its tests handle that case.

## Live-provider verification

**LIVE PROVIDER VERIFICATION REQUIRED.**

The implementation and CI prove S3 protocol behavior against a real S3-compatible server, but no production account has been provisioned or modified by Phase 7B.

Before a live production deploy:

1. create independent staging/production private buckets or prefixes;
2. create least-privilege credentials;
3. configure encryption/lifecycle/versioning policy;
4. configure Railway secrets;
5. verify S3 read/write/copy/delete behavior against the chosen provider, including that conditional `PutObject` and conditional `CopyObject` (`If-None-Match: *`) reject overwrites with `409`/`412` (MinIO was observed rejecting conditional `PutObject` with `412`; conditional `CopyObject` enforcement was not asserted against MinIO — if a provider ignores it, concurrent overwrite is no longer atomically prevented and the provider must not be used without another guard), plus path-style vs virtual-host addressing and the signing region;
6. execute an artifact upload/download/delete smoke through the real ICEHOTT API;
7. confirm no direct public bucket access.

## Deferred

Phase 7B does not implement:

- OpenTelemetry dashboards/alerts — Phase 7C;
- backup/restore and disaster-recovery drills — Phase 7D;
- k6/load/soak and final production release gate — Phase 7E;
- direct-to-browser uploads or presigned URLs;
- multi-provider replication;
- automatic bucket provisioning;
- Redis as a durable authority.

## Completion gate

Phase 7B is ready for merge only after:

- backend Debug and Release suites pass;
- real PostgreSQL tests execute in CI;
- all real S3-compatible integration tests execute in CI;
- frontend and AI gates remain green;
- production container/release-foundation gates remain green;
- formatting, diff and EF model checks pass;
- independent security review has no unresolved blocker;
- PR CI is green on the exact head SHA.
