#!/usr/bin/env bash
# restore-drill.sh — ICEHOTT Phase 7D isolated end-to-end restore drill
#
# Proves the backup-restore procedure using only ephemeral local Docker containers.
# NEVER connects to any live/production service.
#
# Sequence:
#   A  Start isolated dependencies (pgvector, MinIO ×2)
#   B  Apply real ICEHOTT migrations to source PostgreSQL
#   C  Seed representative data (user, workspace, workflow, artifact)
#   D  Record source timestamp
#   E  DB backup + verify
#   F  Artifact backup + verify
#   G  Restore into fresh target DB / target object namespace
#   H  DB restore
#   I  Artifact restore
#   J  Read-only reconciliation against restored target
#   K  Verify representative data in target
#   L  API health probe against restored target (if API available)
#   M  Write machine-readable recovery evidence JSON
#
# Requirements:
#   docker, python3, pg_dump, pg_restore, sha256sum
#   scripts/recovery/requirements.txt installed for Python scripts
#
# Environment (optional overrides):
#   DRILL_POSTGRES_IMAGE    default: pgvector/pgvector:pg16
#   DRILL_MINIO_IMAGE       default: pgsty/minio:RELEASE.2026-08-04T00-00-00Z-amd64@sha256:2b36182f3479c58b5cba920f20479738ee85ce218de0596a244f9a1368268db9
#   DRILL_WORK_DIR          default: /tmp/icehott-drill-<id>
#   GIT_SHA                 optional; embedded in evidence
#
# Exit 0 if RPO <= 3600s and RTO <= 7200s and all checks pass.
# Exit 1 on any failure.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
readonly REPO_ROOT
SOURCE_RELEASE_SHA="${GIT_SHA:-$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || true)}"
[[ "$SOURCE_RELEASE_SHA" =~ ^[0-9a-f]{40}$ ]] || {
  echo "drill: source release SHA must be a full 40-character Git SHA" >&2
  exit 1
}

# ── Images (pinned) ───────────────────────────────────────────────────────────
DRILL_POSTGRES_IMAGE="${DRILL_POSTGRES_IMAGE:-pgvector/pgvector:pg16}"
DRILL_MINIO_IMAGE="${DRILL_MINIO_IMAGE:-pgsty/minio:RELEASE.2026-08-04T00-00-00Z-amd64@sha256:2b36182f3479c58b5cba920f20479738ee85ce218de0596a244f9a1368268db9}"

# ── Drill identity ────────────────────────────────────────────────────────────
DRILL_ID="$(python3 -c "import uuid; print(uuid.uuid4().hex[:12])")"
readonly DRILL_ID
WORK_DIR="${DRILL_WORK_DIR:-/tmp/icehott-drill-${DRILL_ID}}"
case "$WORK_DIR" in
  /tmp/icehott-drill-*|/home/runner/work/_temp/icehott-drill-*) ;;
  *) echo "drill: DRILL_WORK_DIR must be an isolated icehott-drill-* temporary directory" >&2; exit 1 ;;
esac
BACKUP_DIR="$WORK_DIR/pg-backup"
ARTIFACT_MANIFEST_SRC="$WORK_DIR/artifact-backup.manifest.json"
RECONCILE_EVIDENCE="$WORK_DIR/reconcile-evidence.json"
RECOVERY_EVIDENCE="${RECOVERY_EVIDENCE_OUT:-/tmp/icehott-recovery-evidence-${DRILL_ID}.json}"

mkdir -p "$BACKUP_DIR"

# ── Container names ───────────────────────────────────────────────────────────
PG_SRC="icehott-drill-pg-src-${DRILL_ID}"
PG_TGT="icehott-drill-pg-tgt-${DRILL_ID}"
MINIO_SRC="icehott-drill-minio-src-${DRILL_ID}"
MINIO_TGT="icehott-drill-minio-tgt-${DRILL_ID}"

# ── Ports (high ephemeral to avoid collisions) ────────────────────────────────
PG_SRC_PORT="${DRILL_PG_SRC_PORT:-54320}"
PG_TGT_PORT="${DRILL_PG_TGT_PORT:-54321}"
MINIO_SRC_PORT="${DRILL_MINIO_SRC_PORT:-19000}"
MINIO_TGT_PORT="${DRILL_MINIO_TGT_PORT:-19001}"

# ── Credentials (drill-only, ephemeral, never reused) ────────────────────────
PG_USER="drilluser"
PG_PASS="drillpass"
PG_SRC_DB="icehott_drill_src"
PG_TGT_DB="icehott_drill_tgt"
MINIO_USER="drillminioaccess"
MINIO_PASS="drillminiosecret"
SRC_BUCKET="drill-src-artifacts"
BAK_BUCKET="drill-bak-artifacts"
TGT_BUCKET="drill-tgt-artifacts"

SRC_DB_URL="postgresql://${PG_USER}:${PG_PASS}@127.0.0.1:${PG_SRC_PORT}/${PG_SRC_DB}"
TGT_DB_URL="postgresql://${PG_USER}:${PG_PASS}@127.0.0.1:${PG_TGT_PORT}/${PG_TGT_DB}"
MINIO_ENDPOINT_SRC="http://127.0.0.1:${MINIO_SRC_PORT}"
MINIO_ENDPOINT_TGT="http://127.0.0.1:${MINIO_TGT_PORT}"

# ── Cleanup ───────────────────────────────────────────────────────────────────
cleanup() {
  echo "drill: cleaning up containers and work directory..."
  for container in "$PG_SRC" "$PG_TGT" "$MINIO_SRC" "$MINIO_TGT"; do
    docker rm -f "$container" >/dev/null 2>&1 || true
  done
  # Remove API container if it was started
  docker rm -f "icehott-drill-api-${DRILL_ID}" >/dev/null 2>&1 || true
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT

DRILL_START_EPOCH="$(date +%s)"

# ── Dependency checks ─────────────────────────────────────────────────────────
echo "=== DRILL [$DRILL_ID]: checking dependencies ==="
for cmd in docker python3 pg_dump pg_restore psql pg_isready curl sha256sum; do
  command -v "$cmd" &>/dev/null || { echo "drill: required command not found: $cmd" >&2; exit 1; }
done

PYTHON_SCRIPTS=("$SCRIPT_DIR/verify-postgres-backup.py" "$SCRIPT_DIR/backup-artifacts.py"
                "$SCRIPT_DIR/restore-artifacts.py" "$SCRIPT_DIR/reconcile-restore.py")
for script in "${PYTHON_SCRIPTS[@]}"; do
  [[ -f "$script" ]] || { echo "drill: recovery script not found: $script" >&2; exit 1; }
done

# ── A: Start isolated dependencies ───────────────────────────────────────────
echo "=== A: starting isolated Docker dependencies ==="

_start_postgres() {
  local name="$1" port="$2" dbname="$3"
  docker run -d --name "$name" \
    -e POSTGRES_USER="$PG_USER" \
    -e POSTGRES_PASSWORD="$PG_PASS" \
    -e POSTGRES_DB="$dbname" \
    -p "127.0.0.1:${port}:5432" \
    "$DRILL_POSTGRES_IMAGE" >/dev/null
}

_wait_postgres() {
  local name="$1" port="$2" dbname="$3"
  for i in $(seq 1 60); do
    if PGPASSWORD="$PG_PASS" pg_isready -h 127.0.0.1 -p "$port" -U "$PG_USER" -d "$dbname" -q 2>/dev/null; then
      return 0
    fi
    sleep 1
  done
  echo "drill: PostgreSQL $name did not become ready" >&2
  docker logs "$name" >&2
  exit 1
}

_start_minio() {
  local name="$1" port="$2"
  docker run -d --name "$name" \
    -e MINIO_ROOT_USER="$MINIO_USER" \
    -e MINIO_ROOT_PASSWORD="$MINIO_PASS" \
    -p "127.0.0.1:${port}:9000" \
    "$DRILL_MINIO_IMAGE" server /data >/dev/null
}

_wait_minio() {
  local port="$1" name="$2"
  for i in $(seq 1 60); do
    if curl -fsS "http://127.0.0.1:${port}/minio/health/ready" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done
  echo "drill: MinIO $name did not become ready" >&2
  docker logs "$name" >&2
  exit 1
}

_start_postgres "$PG_SRC" "$PG_SRC_PORT" "$PG_SRC_DB"
_start_postgres "$PG_TGT" "$PG_TGT_PORT" "$PG_TGT_DB"
_start_minio    "$MINIO_SRC" "$MINIO_SRC_PORT"
_start_minio    "$MINIO_TGT" "$MINIO_TGT_PORT"

echo "drill: waiting for services..."
_wait_postgres "$PG_SRC" "$PG_SRC_PORT" "$PG_SRC_DB"
_wait_postgres "$PG_TGT" "$PG_TGT_PORT" "$PG_TGT_DB"
_wait_minio "$MINIO_SRC_PORT" "$MINIO_SRC"
_wait_minio "$MINIO_TGT_PORT" "$MINIO_TGT"

echo "drill: all services ready"

# Create source/backup buckets on the source object store and an independent
# target bucket on the target object store.
AWS_ACCESS_KEY_ID="$MINIO_USER" AWS_SECRET_ACCESS_KEY="$MINIO_PASS" \
  python3 -c "
import boto3
source = boto3.client('s3', endpoint_url='http://127.0.0.1:${MINIO_SRC_PORT}', region_name='us-east-1')
target = boto3.client('s3', endpoint_url='http://127.0.0.1:${MINIO_TGT_PORT}', region_name='us-east-1')
for bucket in ['${SRC_BUCKET}', '${BAK_BUCKET}']:
    source.create_bucket(Bucket=bucket)
    print(f'created source-side bucket: {bucket}')
target.create_bucket(Bucket='${TGT_BUCKET}')
print('created target-side bucket')
"

# ── B: Apply ICEHOTT migrations to source DB ──────────────────────────────────
echo "=== B: applying migrations to source DB ==="

# Find the migration bundle or efbundle
MIGRATOR=""

# Try efbundle in backend publish output
for candidate in \
    "$REPO_ROOT/backend/src/ICEHOTT.API/bin/Release/net9.0/efbundle" \
    "$REPO_ROOT/backend/src/ICEHOTT.API/bin/Debug/net9.0/efbundle" \
    "$(command -v efbundle 2>/dev/null || true)"; do
  if [[ -f "$candidate" ]]; then
    MIGRATOR="$candidate"
    break
  fi
done

if [[ -n "$MIGRATOR" ]]; then
  echo "drill: running migrations via efbundle: $MIGRATOR"
  "$MIGRATOR" --connection "$SRC_DB_URL"
else
  # Fallback: dotnet ef database update
  echo "drill: efbundle not found; trying dotnet ef database update..."
  if command -v dotnet &>/dev/null; then
    pushd "$REPO_ROOT/backend/src/ICEHOTT.API" >/dev/null
    ConnectionStrings__DefaultConnection="$SRC_DB_URL" \
      ASPNETCORE_ENVIRONMENT="Development" \
      Database__AutoMigrate="true" \
      dotnet ef database update --no-build 2>/dev/null || \
    ConnectionStrings__DefaultConnection="$SRC_DB_URL" \
      ASPNETCORE_ENVIRONMENT="Development" \
      Database__AutoMigrate="true" \
      dotnet ef database update
    popd >/dev/null
  else
    echo "drill: cannot apply the real ICEHOTT schema because dotnet/efbundle is unavailable" >&2
    exit 1
  fi
fi

# ── C: Seed representative data ───────────────────────────────────────────────
echo "=== C: seeding representative data ==="

# Use Python to generate correct UUIDs and SQL (avoids bash UUID manipulation issues)
read -r SEED_USER_UUID SEED_WS_UUID SEED_ART_UUID SEED_WF_UUID SEED_VER_UUID SEED_RUN_UUID SEED_WF_HASH SEED_STORAGE_KEY SEED_ART_SHA SEED_ART_BYTES SEED_ART_CONTENT_B64 <<< "$(python3 - "$DRILL_ID" <<'PYEOF'
import sys, uuid, hashlib, base64
drill_id = sys.argv[1]

# Deterministic UUIDs for this drill.
user_hex = "d7110000000000000000000000000001"
ws_hex   = "d7110000000000000000000000000002"
art_hex  = "d7110000000000000000000000000003"
wf_hex   = "d7110000000000000000000000000004"
ver_hex  = "d7110000000000000000000000000005"
run_hex  = "d7110000000000000000000000000006"

def fmt(h): return str(uuid.UUID(h))

definition_json = '{"schemaVersion":1,"steps":[]}'
definition_hash = hashlib.sha256(definition_json.encode()).hexdigest()
storage_key = f"objects/{ws_hex}/{art_hex}.bin"
content = f"ICEHOTT drill artifact content {drill_id}".encode()
artifact_sha = hashlib.sha256(content).hexdigest()
b64 = base64.b64encode(content).decode()

print(
    fmt(user_hex), fmt(ws_hex), fmt(art_hex), fmt(wf_hex), fmt(ver_hex), fmt(run_hex),
    definition_hash, storage_key, artifact_sha, len(content), b64
)
PYEOF
)"

# Decode seed content from base64 for upload
SEED_ART_CONTENT="$(python3 -c "import base64, sys; sys.stdout.buffer.write(base64.b64decode('$SEED_ART_CONTENT_B64'))" 2>/dev/null || echo "ICEHOTT drill artifact content $DRILL_ID")"

# Seed via psql using explicit UUID values and correct column names
PGPASSWORD="$PG_PASS" psql -h 127.0.0.1 -p "$PG_SRC_PORT" -U "$PG_USER" -d "$PG_SRC_DB" \
  -v user_id="'$SEED_USER_UUID'" \
  -v ws_id="'$SEED_WS_UUID'" \
  -v art_id="'$SEED_ART_UUID'" \
  -v wf_id="'$SEED_WF_UUID'" \
  -v ver_id="'$SEED_VER_UUID'" \
  -v run_id="'$SEED_RUN_UUID'" \
  -v wf_hash="'$SEED_WF_HASH'" \
  -v storage_key="'$SEED_STORAGE_KEY'" \
  -v sha256="'$SEED_ART_SHA'" \
  -v size_bytes="$SEED_ART_BYTES" \
  <<'SQLEOF'
-- user (NormalizedEmail required, non-null)
INSERT INTO users (
    "Id", "Email", "NormalizedEmail", "PasswordHash", "DisplayName", "CreatedAtUtc"
) VALUES (
    :user_id::uuid, 'drill@icehott.test', 'DRILL@ICEHOTT.TEST',
    'x', 'Drill User', NOW() AT TIME ZONE 'UTC'
) ON CONFLICT DO NOTHING;

-- workspace (CreatedByUserId, not OwnerId)
INSERT INTO workspaces (
    "Id", "Name", "Slug", "CreatedByUserId", "CreatedAtUtc"
) VALUES (
    :ws_id::uuid, 'drill-workspace', 'drill-workspace',
    :user_id::uuid, NOW() AT TIME ZONE 'UTC'
) ON CONFLICT DO NOTHING;

-- workspace membership
INSERT INTO workspace_memberships (
    "WorkspaceId", "UserId", "Role", "JoinedAtUtc"
) VALUES (
    :ws_id::uuid, :user_id::uuid, 'Owner', NOW() AT TIME ZONE 'UTC'
) ON CONFLICT DO NOTHING;

-- Active workflow definition/version and queued run.
INSERT INTO workflow_definitions (
    "Id", "WorkspaceId", "Name", "Description", "Status", "MinimumRunRole",
    "CreatedByUserId", "CreatedAtUtc", "UpdatedAtUtc"
) VALUES (
    :wf_id::uuid, :ws_id::uuid, 'DR drill workflow', 'Phase 7D restore evidence',
    'Active', 1, :user_id::uuid, NOW() AT TIME ZONE 'UTC', NOW() AT TIME ZONE 'UTC'
) ON CONFLICT DO NOTHING;

INSERT INTO workflow_versions (
    "Id", "WorkflowDefinitionId", "WorkspaceId", "VersionNumber",
    "DefinitionJson", "DefinitionHash", "Status", "CreatedByUserId",
    "CreatedAtUtc", "ActivatedAtUtc", "RetiredAtUtc"
) VALUES (
    :ver_id::uuid, :wf_id::uuid, :ws_id::uuid, 1,
    '{"schemaVersion":1,"steps":[]}', :wf_hash, 'Active', :user_id::uuid,
    NOW() AT TIME ZONE 'UTC', NOW() AT TIME ZONE 'UTC', NULL
) ON CONFLICT DO NOTHING;

INSERT INTO workflow_runs (
    "Id", "WorkspaceId", "WorkflowDefinitionId", "WorkflowVersionId",
    "RequestedByUserId", "RunAsUserId", "IdempotencyKey", "Status",
    "CurrentStepKey", "CreatedAtUtc", "LeaseGeneration"
) VALUES (
    :run_id::uuid, :ws_id::uuid, :wf_id::uuid, :ver_id::uuid,
    :user_id::uuid, :user_id::uuid, 'phase7d-drill-run', 'Queued',
    NULL, NOW() AT TIME ZONE 'UTC', 0
) ON CONFLICT DO NOTHING;

-- Ready artifact
INSERT INTO artifacts (
    "Id", "WorkspaceId", "CreatedByUserId",
    "FileName", "ContentType", "SizeBytes", "Sha256",
    "StorageKey", "Status", "CreatedAtUtc"
) VALUES (
    :art_id::uuid,
    :ws_id::uuid,
    :user_id::uuid,
    'drill-artifact.txt', 'text/plain',
    :size_bytes,
    :sha256,
    :storage_key,
    'Ready',
    NOW() AT TIME ZONE 'UTC'
) ON CONFLICT DO NOTHING;
SQLEOF

echo "drill: seed complete: user=$SEED_USER_UUID workspace=$SEED_WS_UUID workflow_run=$SEED_RUN_UUID artifact=$SEED_ART_UUID"
echo "drill: storage_key=$SEED_STORAGE_KEY sha256=$SEED_ART_SHA bytes=$SEED_ART_BYTES"

# Upload seed artifact to source MinIO
AWS_ACCESS_KEY_ID="$MINIO_USER" AWS_SECRET_ACCESS_KEY="$MINIO_PASS" \
  python3 -c "
import boto3, base64
s3 = boto3.client('s3', endpoint_url='${MINIO_ENDPOINT_SRC}', region_name='us-east-1')
content = base64.b64decode('$SEED_ART_CONTENT_B64')
s3.put_object(
    Bucket='${SRC_BUCKET}',
    Key='${SEED_STORAGE_KEY}',
    Body=content,
    ContentLength=len(content),
)
print(f'uploaded seed artifact: ${SEED_STORAGE_KEY} ({len(content)} bytes)')
"

# ── D: Record source timestamp ─────────────────────────────────────────────────
echo "=== D: recording source timestamp ==="
SOURCE_TIMESTAMP="$(python3 -c "from datetime import datetime,timezone; print(datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'))")"
echo "drill: source_timestamp=$SOURCE_TIMESTAMP"

# ── E: DB backup + verify ──────────────────────────────────────────────────────
echo "=== E: PostgreSQL backup ==="
BACKUP_START_EPOCH="$(date +%s)"

ICEHOTT_BACKUP_DATABASE_URL="$SRC_DB_URL" \
  bash "$SCRIPT_DIR/backup-postgres.sh" --backup-dir "$BACKUP_DIR" --release-sha "$SOURCE_RELEASE_SHA"

# Find the manifest
PG_MANIFEST="$(find "$BACKUP_DIR" -name "*.manifest.json" -not -name "*.tmp*" | sort | tail -1)"
[[ -z "$PG_MANIFEST" ]] && { echo "drill: no backup manifest found" >&2; exit 1; }
echo "drill: manifest=$PG_MANIFEST"

echo "=== E: verifying backup ==="
python3 "$SCRIPT_DIR/verify-postgres-backup.py" "$PG_MANIFEST" --max-age-seconds 3600

BACKUP_SHA256="$(python3 -c "import json; m=json.load(open('$PG_MANIFEST')); print(m['sha256'])")"
BACKUP_ID="$(python3 -c "import json; m=json.load(open('$PG_MANIFEST')); print(m['backup_id'])")"
BACKUP_CREATED_UTC="$(python3 -c "import json; m=json.load(open('$PG_MANIFEST')); print(m['created_at_utc'])")"

# ── F: Artifact backup + verify ───────────────────────────────────────────────
echo "=== F: artifact backup ==="
ICEHOTT_SOURCE_S3_ACCESS_KEY_ID="$MINIO_USER" \
ICEHOTT_SOURCE_S3_SECRET_ACCESS_KEY="$MINIO_PASS" \
ICEHOTT_BACKUP_S3_ACCESS_KEY_ID="$MINIO_USER" \
ICEHOTT_BACKUP_S3_SECRET_ACCESS_KEY="$MINIO_PASS" \
  python3 "$SCRIPT_DIR/backup-artifacts.py" \
    --source-bucket "$SRC_BUCKET" \
    --backup-bucket "$BAK_BUCKET" \
    --source-endpoint "$MINIO_ENDPOINT_SRC" \
    --backup-endpoint "$MINIO_ENDPOINT_SRC" \
    --manifest-out "$ARTIFACT_MANIFEST_SRC" \
    --release-sha "$SOURCE_RELEASE_SHA"

ARTIFACT_BACKUP_SET_ID="$(python3 -c "import json; m=json.load(open('$ARTIFACT_MANIFEST_SRC')); print(m['backup_set_id'])")"
ARTIFACT_OBJECT_COUNT="$(python3 -c "import json; m=json.load(open('$ARTIFACT_MANIFEST_SRC')); print(m['object_count'])")"
ARTIFACT_TOTAL_BYTES="$(python3 -c "import json; m=json.load(open('$ARTIFACT_MANIFEST_SRC')); print(m['total_bytes'])")"
ARTIFACT_MANIFEST_SHA="$(sha256sum "$ARTIFACT_MANIFEST_SRC" | awk '{print $1}')"

echo "drill: artifact_backup_set_id=$ARTIFACT_BACKUP_SET_ID objects=$ARTIFACT_OBJECT_COUNT"

# ── G: Restore into fresh target DB / target object namespace ─────────────────
echo "=== G: preparing fresh targets ==="
RESTORE_START_EPOCH="$(date +%s)"

# Target DB already exists (started in step A); confirm it is accessible
PGPASSWORD="$PG_PASS" psql -h 127.0.0.1 -p "$PG_TGT_PORT" -U "$PG_USER" -d "$PG_TGT_DB" \
  -c "SELECT 1;" >/dev/null

echo "drill: target DB accessible: $PG_TGT_DB"

# ── H: DB restore ─────────────────────────────────────────────────────────────
echo "=== H: PostgreSQL restore ==="
# restore-postgres.sh uses env vars (never CLI args) for the URL and confirmation.
# The target DB is named '*_drill_*' which satisfies the isolation keyword check.
ICEHOTT_RESTORE_DATABASE_URL="$TGT_DB_URL" \
  ICEHOTT_RESTORE_CONFIRM="RESTORE" \
  bash "$SCRIPT_DIR/restore-postgres.sh" \
    --manifest "$PG_MANIFEST"

echo "drill: DB restore complete"

# ── I: Artifact restore ───────────────────────────────────────────────────────
echo "=== I: artifact restore ==="
# Backup and restore targets are intentionally served by separate MinIO
# instances to prove provider/namespace separation.
ICEHOTT_BACKUP_S3_ACCESS_KEY_ID="$MINIO_USER" \
ICEHOTT_BACKUP_S3_SECRET_ACCESS_KEY="$MINIO_PASS" \
ICEHOTT_TARGET_S3_ACCESS_KEY_ID="$MINIO_USER" \
ICEHOTT_TARGET_S3_SECRET_ACCESS_KEY="$MINIO_PASS" \
  python3 "$SCRIPT_DIR/restore-artifacts.py" \
    --manifest "$ARTIFACT_MANIFEST_SRC" \
    --backup-bucket "$BAK_BUCKET" \
    --target-bucket "$TGT_BUCKET" \
    --backup-endpoint "$MINIO_ENDPOINT_SRC" \
    --target-endpoint "$MINIO_ENDPOINT_TGT"

echo "drill: artifact restore complete"

# ── J: Read-only reconciliation ───────────────────────────────────────────────
echo "=== J: read-only reconciliation ==="
ICEHOTT_RECONCILE_DATABASE_URL="$TGT_DB_URL" \
ICEHOTT_TARGET_S3_ACCESS_KEY_ID="$MINIO_USER" \
ICEHOTT_TARGET_S3_SECRET_ACCESS_KEY="$MINIO_PASS" \
  python3 "$SCRIPT_DIR/reconcile-restore.py" \
    --bucket "$TGT_BUCKET" \
    --endpoint "$MINIO_ENDPOINT_TGT" \
    --evidence-out "$RECONCILE_EVIDENCE" \
    --strict

echo "drill: reconciliation passed"

# ── K: Verify representative data in target ────────────────────────────────────
echo "=== K: verifying representative data in target DB ==="
VERIFY_ROWS="$(PGPASSWORD="$PG_PASS" psql -h 127.0.0.1 -p "$PG_TGT_PORT" -U "$PG_USER" -d "$PG_TGT_DB" \
  -t -A -c "SELECT COUNT(*) FROM artifacts WHERE \"StorageKey\" = '$SEED_STORAGE_KEY' AND \"Status\" = 'Ready' AND \"Sha256\" = '$SEED_ART_SHA' AND \"SizeBytes\" = $SEED_ART_BYTES;")"
[[ "$VERIFY_ROWS" == "1" ]] || {
  echo "drill: FAIL: expected 1 artifact row in target, got: $VERIFY_ROWS" >&2
  exit 1
}
echo "drill: artifact row verified in target DB"

VERIFY_USERS="$(PGPASSWORD="$PG_PASS" psql -h 127.0.0.1 -p "$PG_TGT_PORT" -U "$PG_USER" -d "$PG_TGT_DB" \
  -t -A -c "SELECT COUNT(*) FROM users WHERE \"Email\" = 'drill@icehott.test';")"
[[ "$VERIFY_USERS" == "1" ]] || {
  echo "drill: FAIL: expected 1 user in target, got: $VERIFY_USERS" >&2
  exit 1
}
echo "drill: user row verified in target DB"

VERIFY_WORKFLOW="$(PGPASSWORD="$PG_PASS" psql -h 127.0.0.1 -p "$PG_TGT_PORT" -U "$PG_USER" -d "$PG_TGT_DB"   -t -A -c "SELECT COUNT(*) FROM workflow_runs r JOIN workflow_versions v ON v.\"Id\" = r.\"WorkflowVersionId\" AND v.\"WorkspaceId\" = r.\"WorkspaceId\" JOIN workflow_definitions d ON d.\"Id\" = r.\"WorkflowDefinitionId\" AND d.\"WorkspaceId\" = r.\"WorkspaceId\" WHERE r.\"Id\" = '$SEED_RUN_UUID'::uuid AND r.\"WorkspaceId\" = '$SEED_WS_UUID'::uuid AND r.\"Status\" = 'Queued' AND v.\"Status\" = 'Active' AND d.\"Status\" = 'Active';")"
[[ "$VERIFY_WORKFLOW" == "1" ]] || {
  echo "drill: FAIL: restored workflow definition/version/run integrity check failed" >&2
  exit 1
}
echo "drill: workflow definition/version/run verified in target DB"

# RTO ends only after restored DB, object bytes, reconciliation and representative
# tenant/workflow/artifact integrity have all been verified.
RESTORE_END_EPOCH="$(date +%s)"

# ── L: Recovery target is usable by application-level integrity checks ────────
echo "=== L: restored application data integrity verified ==="
DRILL_END_EPOCH="$(date +%s)"

# Calculate RPO (time from source_timestamp to backup creation)
BACKUP_CREATED_EPOCH="$(python3 -c "
from datetime import datetime, timezone
dt = datetime.strptime('$BACKUP_CREATED_UTC', '%Y-%m-%dT%H:%M:%SZ').replace(tzinfo=timezone.utc)
import time
print(int(dt.timestamp()))
")"
SOURCE_EPOCH="$(python3 -c "
from datetime import datetime, timezone
dt = datetime.strptime('$SOURCE_TIMESTAMP', '%Y-%m-%dT%H:%M:%SZ').replace(tzinfo=timezone.utc)
import time
print(int(dt.timestamp()))
")"
RPO_SECONDS=$(( BACKUP_CREATED_EPOCH - SOURCE_EPOCH ))
[[ $RPO_SECONDS -lt 0 ]] && RPO_SECONDS=0
RTO_SECONDS=$(( RESTORE_END_EPOCH - RESTORE_START_EPOCH ))
TOTAL_DRILL_SECONDS=$(( DRILL_END_EPOCH - DRILL_START_EPOCH ))

echo "drill: RPO_seconds=$RPO_SECONDS (target<=3600)"
echo "drill: RTO_seconds=$RTO_SECONDS (target<=7200)"

RPO_PASS=false; [[ $RPO_SECONDS -le 3600 ]] && RPO_PASS=true
RTO_PASS=false; [[ $RTO_SECONDS -le 7200 ]] && RTO_PASS=true

# ── M: Write recovery evidence JSON ──────────────────────────────────────────
echo "=== M: writing recovery evidence ==="
RESTORE_STARTED_UTC="$(python3 -c "
from datetime import datetime, timezone
import sys
print(datetime.fromtimestamp($RESTORE_START_EPOCH, tz=timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'))
")"
RESTORE_COMPLETED_UTC="$(python3 -c "
from datetime import datetime, timezone
print(datetime.fromtimestamp($RESTORE_END_EPOCH, tz=timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'))
")"

python3 - <<PYEOF
import json, hashlib, sys
from pathlib import Path
sys.path.insert(0, "$SCRIPT_DIR")
from recovery_common import atomic_write_text, safe_local_output_path

reconcile_evidence = {}
try:
    reconcile_evidence = json.loads(Path('$RECONCILE_EVIDENCE').read_text())
except Exception:
    pass

evidence = {
    "schema_version": 1,
    "tool": "restore-drill",
    "drill_id": "$DRILL_ID",
    "source_release_sha": "$SOURCE_RELEASE_SHA",
    "source_timestamp": "$SOURCE_TIMESTAMP",
    "backup_created_utc": "$BACKUP_CREATED_UTC",
    "restore_started_utc": "$RESTORE_STARTED_UTC",
    "restore_completed_utc": "$RESTORE_COMPLETED_UTC",
    "db_backup_sha256": "$BACKUP_SHA256",
    "artifact_backup": {
        "manifest_sha256": "$ARTIFACT_MANIFEST_SHA",
        "object_count": $ARTIFACT_OBJECT_COUNT,
        "total_bytes": $ARTIFACT_TOTAL_BYTES,
    },
    "reconciliation": reconcile_evidence.get("counts", {}),
    "rpo_seconds": $RPO_SECONDS,
    "rto_seconds": $RTO_SECONDS,
    "target_rpo_seconds": 3600,
    "target_rto_seconds": 7200,
    "rpo_pass": $( [[ "$RPO_PASS" == "true" ]] && echo "True" || echo "False" ),
    "rto_pass": $( [[ "$RTO_PASS" == "true" ]] && echo "True" || echo "False" ),
    "reconciliation_pass": reconcile_evidence.get("pass", False),
    "drill_pass": (
        $( [[ "$RPO_PASS" == "true" && "$RTO_PASS" == "true" ]] && echo "True" || echo "False" )
        and reconcile_evidence.get("pass", False)
    ),
    "ci_note": "CI drill proves procedure; production-provider throughput requires live drill",
}

# Safety assertion: no drill credentials in evidence
text = json.dumps(evidence, indent=2)
forbidden_values = ["drillpass", "drillminiosecret", "drillminioaccess"]
for fv in forbidden_values:
    if fv in text:
        print(f"FATAL: credential value '{fv}' found in evidence", file=sys.stderr)
        sys.exit(1)

print(text)
Path('$RECOVERY_EVIDENCE').write_text(text + "\n")
print(f"drill: evidence written to $RECOVERY_EVIDENCE", file=sys.stderr)
PYEOF

# ── Assert RPO/RTO ────────────────────────────────────────────────────────────
[[ "$RPO_PASS" == "true" ]] || {
  echo "drill: FAIL RPO=${RPO_SECONDS}s exceeds target=3600s" >&2
  exit 1
}
[[ "$RTO_PASS" == "true" ]] || {
  echo "drill: FAIL RTO=${RTO_SECONDS}s exceeds target=7200s" >&2
  exit 1
}

echo ""
echo "=== DRILL COMPLETE ==="
echo "  drill_id=$DRILL_ID"
echo "  rpo_seconds=$RPO_SECONDS (target<=3600): PASS"
echo "  rto_seconds=$RTO_SECONDS (target<=7200): PASS"
echo "  evidence=$RECOVERY_EVIDENCE"
echo ""
echo "NOTE: This drill proves procedure correctness using local Docker containers."
echo "      It does NOT prove production-provider throughput."
