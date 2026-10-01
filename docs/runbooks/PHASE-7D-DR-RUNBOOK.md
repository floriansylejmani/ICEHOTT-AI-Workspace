# ICEHOTT DR Runbook — Phase 7D

**Version:** Phase 7D
**Owner:** Platform/SRE team
**Last updated:** 2026-10-01

This runbook covers the response procedure for each major failure class.
For backup/restore tooling reference, see [PHASE-7D-BACKUP-RESTORE-DR.md](../PHASE-7D-BACKUP-RESTORE-DR.md).

---

## 1. API container failure

**Detection**
- `/health` returns non-200 for >2 minutes (alert: `ICEHOTTApiDown`)
- Railway service shows crash loop or failed health probe

**User impact**
- API requests time out or return 502/503
- Web application shows error states

**First response**
1. Check Railway service logs for startup error or OOM
2. Check if `DATABASE_URL` / `JWT__KEY` / required env vars are present
3. Check if PostgreSQL is reachable from the API service

**Fail-closed behavior**
- The API startup validates configuration and exits with a non-zero code if required secrets are missing or unsafe
- A crash on startup does not expose partial state to clients

**Recovery/rollback**
1. If a bad release: redeploy the previous known-good Git SHA via Railway dashboard
2. If a configuration issue: correct the Railway environment variable and redeploy
3. Run `scripts/release/smoke.sh <base-url> <expected-sha>` to confirm health

**Verification**
- `/health` returns 200
- `/ready` returns 200
- `/release` shows the correct Git SHA

**Escalation**
If the API is down for >15 minutes and root cause is unclear, escalate to engineering on-call.

---

## 2. AI service failure

**Detection**
- FastAPI `/health` returns non-200
- API returns errors on RAG / embedding endpoints

**User impact**
- Conversation intelligence features unavailable
- RAG retrieval fails; fallback rate spike visible in metrics
- Workflow steps that require AI may fail or pause

**First response**
1. Check `icehott-ai` Railway logs
2. AI service failure is isolated: the API, PostgreSQL and artifacts are unaffected
3. Check if embedding provider API key is still valid

**Fail-closed behavior**
- The API returns appropriate error codes for AI-dependent requests
- Durable workflow/artifact/auth state is unaffected

**Recovery/rollback**
1. Redeploy the previous AI image SHA if a bad release
2. Rotate the embedding provider API key if leaked (see § 8 — Leaked credentials)

**Verification**
- AI `/health` returns 200
- A test embedding request succeeds

**Escalation**
If the AI service is unavailable >30 minutes, escalate.

---

## 3. Worker failure

**Detection**
- Oldest queued workflow item age >5 minutes (alert: `ICEHOTTWorkflowBacklog`)
- Knowledge ingestion queue depth growing (alert: `ICEHOTTKnowledgeQueueDepth`)

**User impact**
- Workflow runs queued but not executing
- Knowledge ingestion delayed

**First response**
1. Check `icehott-worker` Railway logs
2. The worker uses PostgreSQL lease fencing — a restarted worker safely reclaims leases after expiry
3. Redis is not authoritative; worker restart is safe

**Fail-closed behavior**
- PostgreSQL-leased workflow runs are not duplicated on worker restart
- `OutcomeUnknown` is used for uncertain tool/step results

**Recovery/rollback**
1. Redeploy previous worker SHA if a bad release
2. If worker is healthy but workflows are stuck: check for expired leases in `workflow_runs`

**Verification**
- Workflow queue depth drains
- Knowledge ingestion resumes

**Escalation**
If workflows remain stuck >30 minutes after worker restart, escalate.

---

## 4. PostgreSQL loss/corruption

**Detection**
- API `/ready` returns 503 with DB error
- Database connection failures in metrics

**User impact**
- All API requests fail (auth, workflows, artifacts, RAG all require PostgreSQL)
- Complete service outage

**First response**
1. Confirm PostgreSQL is down vs. misconfigured vs. network issue
2. If a configuration/network issue: restore connectivity first (no data restore needed)
3. If data loss/corruption: activate DR procedure below

**Restore procedure**
```
# 1. Identify most recent verified backup
ls -lt /path/to/backups/*.manifest.json

# 2. Verify the backup before using it
python3 scripts/recovery/verify-postgres-backup.py /path/to/icehott-<id>.manifest.json

# 3. Ensure the target DB exists (Railway provisions it; do NOT drop/create production manually)
# The target DB name must be confirmed with the team before proceeding

# 4. Restore to the target (requires ICEHOTT_ALLOW_DANGEROUS_RESTORE=true for production names)
ICEHOTT_RESTORE_DATABASE_URL="<production-restore-dsn-from-secret-store>" \
  ICEHOTT_RESTORE_CONFIRM="RESTORE" \
  ICEHOTT_ALLOW_DANGEROUS_RESTORE="true" \
  bash scripts/recovery/restore-postgres.sh \
    --manifest /path/to/icehott-<id>.manifest.json

# 5. Run reconciliation against restored DB and object storage.
# Supply all credentials through the incident-approved secret store/environment.
export ICEHOTT_RECONCILE_DATABASE_URL="<restored-db-dsn-from-secret-store>"
export ICEHOTT_TARGET_S3_ACCESS_KEY_ID="<target-access-key-from-secret-store>"
export ICEHOTT_TARGET_S3_SECRET_ACCESS_KEY="<target-secret-key-from-secret-store>"
python3 scripts/recovery/reconcile-restore.py \
  --bucket <restored-private-bucket> \
  --endpoint <restored-s3-endpoint> \
  --evidence-out /tmp/icehott-reconcile-evidence.json \
  --strict
```

**Fail-closed behavior**
- The restore script requires an explicit confirmation and isolation gate
- Destructive production restore requires an additional override flag and **incident approval**

**Verification**
- API `/ready` returns 200
- Reconciliation passes
- Spot-check representative user/workspace/artifact data

**Escalation**
Activate incident management before production restore. Record: incident SHA, restore manifest ID, timestamp.

---

## 5. Object storage outage/corruption

**Detection**
- Artifact upload/download failures
- Object storage unavailable alert
- Reconciliation detects missing/corrupt objects

**User impact**
- Artifact upload fails
- Artifact download fails
- Workflows that depend on artifacts may pause

**First response**
1. Confirm if the outage is provider-wide or bucket-specific
2. Check that the `ObjectStorage:*` configuration is correct
3. If provider-wide: wait for provider recovery; application will return 503 for storage operations

**Recovery from corruption**
```
# 1. Identify missing/corrupt objects via read-only reconciliation.
export ICEHOTT_RECONCILE_DATABASE_URL="<live-db-dsn-from-secret-store>"
export ICEHOTT_TARGET_S3_ACCESS_KEY_ID="<live-object-access-key>"
export ICEHOTT_TARGET_S3_SECRET_ACCESS_KEY="<live-object-secret-key>"
python3 scripts/recovery/reconcile-restore.py \
  --bucket <live-private-bucket> \
  --endpoint <live-s3-endpoint> \
  --evidence-out /tmp/icehott-reconcile-before.json

# 2. Restore only from a previously verified artifact manifest. Existing matching
# objects are idempotent; any mismatched destination object fails closed.
export ICEHOTT_BACKUP_S3_ACCESS_KEY_ID="<backup-access-key>"
export ICEHOTT_BACKUP_S3_SECRET_ACCESS_KEY="<backup-secret-key>"
python3 scripts/recovery/restore-artifacts.py \
  --manifest /path/to/artifact-backup.manifest.json \
  --backup-bucket <backup-private-bucket> \
  --target-bucket <live-private-bucket> \
  --backup-endpoint <backup-s3-endpoint> \
  --target-endpoint <live-s3-endpoint> \
  --allow-non-empty-target

# 3. Verify reconciliation passes.
python3 scripts/recovery/reconcile-restore.py \
  --bucket <live-private-bucket> \
  --endpoint <live-s3-endpoint> \
  --evidence-out /tmp/icehott-reconcile-after.json \
  --strict
```

**Fail-closed behavior**
- The restore tool will not overwrite objects with different SHA-256
- No public ACL is set on restored objects
- Authorization remains DB-authoritative; restored objects do not grant access

**Verification**
- Reconciliation passes with no missing/corrupt objects
- A representative artifact round-trip succeeds

**Escalation**
If widespread corruption affects >10% of artifacts, escalate to engineering on-call.

---

## 6. Bad application release

**Detection**
- 5xx error rate spike after deployment
- Smoke check fails on the new release
- `/release` shows wrong Git SHA

**User impact**
- API requests failing with 5xx
- Possible partial availability

**First response**
1. Immediately identify the failing Git SHA from `/release`
2. Do NOT wait — redeploy the previous known-good SHA within 5 minutes of detection

**Rollback procedure**
```
# Via Railway dashboard: select the previous deployment and promote it
# OR: re-run the deploy workflow with the previous known-good SHA

# Verify after rollback:
bash scripts/release/smoke.sh <api-url> <previous-sha>
```

**Fail-closed behavior**
- The API validates configuration on startup; a fatally misconfigured build exits cleanly
- The previous deployment continues serving until the rollback deploys

**Verification**
- Smoke check passes on the previous SHA
- Error rate returns to baseline

**Escalation**
If rollback does not resolve the issue, escalate; the schema may need attention (see § 7).

---

## 7. Failed schema migration

**Detection**
- Migration job exits non-zero in the CI/CD pipeline
- API startup logs show EF migration failure

**User impact**
- New deployment is blocked; the previous version continues serving
- If a partial migration ran: schema may be in an inconsistent state

**First response**
1. Do not retry the migration without investigating
2. Check migration logs for the specific error (constraint violation, column conflict, etc.)
3. If the migration ran partially: **stop all traffic to the database** before any correction

**Recovery options**
| Scenario | Action |
|---|---|
| Migration not yet applied | Fix the migration, re-run |
| Migration partially applied | Restore a pre-migration backup to isolated env; validate; then decide on forward-fix or rollback |
| Migration applied but app broke | Roll back the application (previous SHA); schema stays as-is |

**Never**
- Never auto-rollback a schema change without explicit validation
- Never apply `dotnet ef database update` against production on startup (`Database__AutoMigrate=false`)

**Verification**
- Migration job exits 0
- API `/ready` returns 200 with new SHA

**Escalation**
Partial migration to production requires incident management and engineering on-call.

---

## 8. Leaked/revoked credentials

**Detection**
- Provider security alert
- Unexpected authentication failures from the live service
- Rotation performed by a team member

**User impact**
- Service may temporarily fail if credentials expire before rotation completes

**Response (by credential type)**

**JWT signing key**
1. Generate a new key (≥ 32 bytes, cryptographically random)
2. Update the Railway `JWT__KEY` variable
3. All existing sessions will be invalidated (users re-authenticate)
4. Redeploy the API

**PostgreSQL credentials**
1. Rotate credentials in Railway (or Railway managed DB)
2. Update `DATABASE_URL` / `ConnectionStrings__DefaultConnection`
3. Redeploy API and worker

**Object storage credentials**
1. Revoke the old access key in the storage provider console
2. Generate a new least-privilege key
3. Update `ObjectStorage__AccessKeyId` and `ObjectStorage__SecretAccessKey` in Railway
4. Rotate separate backup-role credentials as needed; do not reuse the live runtime key
5. Redeploy API and worker
6. Run a read-only reconciliation check to confirm storage is accessible

**Observability / OTLP credentials**
1. Revoke the leaked collector/backend token or header value
2. Update the secret-backed OTLP header configuration
3. Restart API, worker, AI and collector as required
4. Verify telemetry export without placing the credential in logs or traces
5. Treat telemetry loss as a warning unless it masks a separate service incident

**Fail-closed behavior**
- The API refuses to start with empty/default credentials in production
- An invalid credential produces an auth error, not silent data exposure

**Verification**
- API `/ready` returns 200 with the new credentials
- Artifact round-trip passes

**Escalation**
If the source and scope of credential leak is unknown, treat as a security incident.

---

## 9. Regional/provider outage

**Detection**
- Multiple services unreachable simultaneously
- Provider status page shows active incident

**User impact**
- Partial or complete service outage depending on scope

**First response**
1. Confirm on the provider status page (Railway, Vercel, storage provider)
2. This is typically a wait-for-recovery situation
3. ICEHOTT is not currently multi-region active/active

**Fail-closed behavior**
- The application fails closed during provider outage; no data corruption
- PostgreSQL and artifact state is preserved on recovery

**Recovery**
- On provider recovery: services restart automatically if health probes pass
- If data was lost provider-side: activate PostgreSQL and artifact restore procedures (§ 4, § 5)

**Escalation**
If outage exceeds the provider's stated SLA, engage provider support.

---

## 10. Backup integrity failure

**Detection**
- `verify-postgres-backup.py` exits non-zero
- SHA-256 mismatch detected during scheduled verification
- `reconcile-restore.py` reports missing/corrupt objects
- Drill evidence shows `rpo_pass=false` or `rto_pass=false`

**User impact**
- No immediate user impact if live services are healthy
- Recovery capability is compromised

**First response**
1. **Do not delete the suspect backup** — preserve it for investigation
2. Immediately attempt a new backup to confirm the backup system is working
3. Verify the new backup manually

**Investigation**
```
# Verify suspect backup
python3 scripts/recovery/verify-postgres-backup.py /path/to/manifest.json

# Check for storage corruption on the backup target
# (check bucket policy, encryption, object integrity)
```

**Recovery**
1. If backup system is broken: fix and run a fresh backup immediately
2. If a single backup is corrupt: rely on the previous verified backup
3. Never declare a backup operational without a successful verification

**Escalation**
If backup integrity cannot be restored within 2 hours, escalate; RPO targets are at risk.

---

## 11. Backup destination unavailable

**Detection**
- Scheduled logical backup fails before publishing a verified manifest
- Backup bucket/directory cannot be reached
- Retention or scheduled verification reports the newest valid backup is approaching the one-hour RPO budget

**User impact**
- Live traffic may remain healthy, but recovery capability is degrading and the RPO objective is at risk

**First response**
1. Preserve the most recent verified backup; do not prune while the destination is unhealthy
2. Confirm whether the failure is credentials, network, quota/capacity or provider-wide
3. Restore access or switch to the pre-approved secondary encrypted backup destination
4. Run a fresh backup and verification immediately after service is restored

**Fail-closed behavior**
- Failed backup attempts do not publish a valid-looking manifest
- No backup is considered valid merely because a dump/object exists

**Verification**
- New logical PostgreSQL backup passes structural, size and SHA checks
- Artifact backup manifest/object verification passes
- Newest verified recovery point is within 3600 seconds
- A scheduled or operator-triggered restore drill is queued if the outage crossed an RPO boundary

**Escalation**
Escalate before the newest verified backup exceeds the one-hour RPO target, or immediately if both primary and secondary backup destinations are unavailable.

---

## Contact

On-call rotation and escalation paths are maintained in the team's incident management system. This runbook does not contain contact details — keep those in a system with access control.
