#!/usr/bin/env bash
# Executable verification of the ICEHOTT release images. Runs locally and in CI with only
# Docker available; it starts its own throwaway pgvector database and removes everything.
#
#   verify-container.sh <api-image> <migrator-image> <expected-git-sha>
#
# Proves, against the real images:
#   1. the runtime image runs as a non-root user and carries the release revision label;
#   2. the image fails closed (safe message, no secrets) when started without configuration;
#   3. the migration bundle applies every migration, is idempotent, and fails on bad credentials;
#   4. the same image starts in the Api role and in the Worker role, each with only its own duties;
#   5. the smoke check reports the expected Git SHA for both roles.
set -euo pipefail

api_image="${1:?usage: verify-container.sh <api-image> <migrator-image> <expected-git-sha>}"
migrator_image="${2:?}"
expected_sha="${3:?}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"

run_id="$$"
network="icehott-verify-$run_id"
database="$network-pg"
api_name="$network-api"
worker_name="$network-worker"
pg_image="${VERIFY_PG_IMAGE:-pgvector/pgvector:pg16}"

random() { head -c 96 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c "$1"; }
db_password="$(random 24)"
jwt_key="$(random 48)"

cleanup() {
  docker rm -f "$api_name" "$worker_name" "$database" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail() { echo "verify-container: FAIL - $*" >&2; exit 1; }

echo "== 1. image hardening"
user="$(docker inspect --format '{{.Config.User}}' "$api_image")"
[[ -n "$user" && "$user" != "root" && "$user" != "0" ]] || fail "runtime image runs as root (user='$user')"
revision="$(docker inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$api_image")"
[[ "$revision" == "$expected_sha" ]] || fail "image revision label does not match the expected SHA"
env_dump="$(docker inspect --format '{{json .Config.Env}}' "$api_image")"
grep -q 'ASPNETCORE_ENVIRONMENT=Production' <<<"$env_dump" || fail "image does not default to Production"
grep -qiE 'Jwt__Key|Password|ConnectionStrings' <<<"$env_dump" && fail "image environment carries secret-shaped settings"
echo "   user=$user revision=$revision environment=Production"

echo "== 2. fail-closed startup without configuration"
set +e
bare_output="$(docker run --rm "$api_image" 2>&1)"
bare_status=$?
set -e
[[ $bare_status -ne 0 ]] || fail "image started without any configuration"
grep -q 'Production configuration validation failed' <<<"$bare_output" || fail "missing the safe validation message"
echo "   refused to start (exit $bare_status) with a validation summary"

echo "== 3. migration bundle"
docker network create "$network" >/dev/null
docker run -d --name "$database" --network "$network" \
  -e POSTGRES_USER=icehott_app -e POSTGRES_PASSWORD="$db_password" -e POSTGRES_DB=icehott \
  "$pg_image" >/dev/null
for _ in $(seq 1 40); do
  docker exec "$database" pg_isready -U icehott_app -d icehott >/dev/null 2>&1 && break
  sleep 1
done
docker exec "$database" pg_isready -U icehott_app -d icehott >/dev/null || fail "database did not become ready"

connection="Host=$database;Port=5432;Database=icehott;Username=icehott_app;Password=$db_password"
docker run --rm --network "$network" "$migrator_image" --connection "$connection" >/dev/null
applied="$(docker exec "$database" psql -U icehott_app -d icehott -tAc 'select count(*) from "__EFMigrationsHistory"')"
expected="$(find "$repo_root/backend/src/ICEHOTT.Persistence/Migrations" -name '*.cs' \
  ! -name '*.Designer.cs' ! -name '*Snapshot.cs' | wc -l | tr -d ' ')"
[[ "$applied" == "$expected" ]] || fail "applied $applied migrations, repository has $expected"
second_run="$(docker run --rm --network "$network" "$migrator_image" --connection "$connection" 2>&1)"
grep -q 'No migrations were applied' <<<"$second_run" || fail "second migration run was not a no-op"
set +e
docker run --rm --network "$network" "$migrator_image" \
  --connection "Host=$database;Database=icehott;Username=icehott_app;Password=wrong-$(random 8)" >/dev/null 2>&1
bad_status=$?
set -e
[[ $bad_status -ne 0 ]] || fail "migration bundle succeeded with bad credentials"
echo "   applied=$applied idempotent=yes bad-credential-exit=$bad_status"

echo "== 4/5. Api and Worker roles from one image"
public_host="api.verify.test"
common_env=(
  -e Deployment__Tier=staging
  -e "Release__GitSha=$expected_sha"
  -e "ConnectionStrings__DefaultConnection=$connection"
  -e Database__RequireTransportSecurity=false
  -e AiRuntime__BaseUrl=http://icehott-ai.internal:8000
  -e ArtifactStorage__Provider=Local
  -e ProductionSafety__AllowLocalArtifactStorage=true
)
# Only the API holds token-signing material, browser origins, host filtering and proxy trust;
# the worker below is deliberately started WITHOUT any of them.
api_only_env=(
  -e "Jwt__Key=$jwt_key"
  -e Cors__AllowedOrigins__0=https://app.verify.test
  -e "AllowedHosts=$public_host"
  -e ForwardedHeaders__Enabled=true
  -e ForwardedHeaders__TrustedNetworks__0=10.0.0.0/8
)
docker run -d --name "$api_name" --network "$network" -p 127.0.0.1::8080   "${common_env[@]}" "${api_only_env[@]}" -e Service__Role=Api "$api_image" >/dev/null
docker run -d --name "$worker_name" --network "$network" -p 127.0.0.1::8080   "${common_env[@]}" -e Service__Role=Worker "$api_image" >/dev/null

port_of() { docker port "$1" 8080/tcp | head -n1 | sed 's/.*://'; }
HOST_HEADER="$public_host" "$here/smoke.sh" "http://127.0.0.1:$(port_of "$api_name")" "$expected_sha"
"$here/smoke.sh" "http://127.0.0.1:$(port_of "$worker_name")" "$expected_sha"

api_logs="$(docker logs "$api_name" 2>&1)"
worker_logs="$(docker logs "$worker_name" 2>&1)"
grep -qiE 'runner|scheduler|ingestion worker|maintenance worker' <<<"$api_logs" \
  && fail "the Api role started background services"
for duty in 'Workflow runner' 'Workflow scheduler' 'Knowledge ingestion worker' 'Artifact maintenance worker'; do
  grep -q "$duty" <<<"$worker_logs" || fail "the Worker role did not start: $duty"
done
worker_code="$(curl -sS -o /dev/null -w '%{http_code}' -H "Host: $public_host" \
  "http://127.0.0.1:$(port_of "$worker_name")/api/auth/login" -X POST)"
[[ "$worker_code" == "404" ]] || fail "the Worker role serves API routes (HTTP $worker_code)"
echo "   api: no workers started; worker: started without JWT/CORS/proxy settings, all durable workers running, API routes 404"

echo "verify-container: PASS"
