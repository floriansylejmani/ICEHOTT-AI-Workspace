#!/usr/bin/env bash
# Local end-to-end proof for Phase 7C:
#   client (traceparent) -> ASP.NET API -> FastAPI AI service -> OTEL Collector
# and that a stopped collector does not break either service.
#
# Requires Docker Compose. Uses non-default host ports so it can run next to other stacks.
set -euo pipefail

cd "$(dirname "$0")/../.."

export API_PORT="${API_PORT:-15050}" AI_PORT="${AI_PORT:-18000}" POSTGRES_PORT="${POSTGRES_PORT:-15432}" \
  REDIS_PORT="${REDIS_PORT:-16379}" OTEL_GRPC_PORT="${OTEL_GRPC_PORT:-14317}" OTEL_HTTP_PORT="${OTEL_HTTP_PORT:-14318}" \
  OTEL_PROMETHEUS_PORT="${OTEL_PROMETHEUS_PORT:-18889}" OTEL_HEALTH_PORT="${OTEL_HEALTH_PORT:-13134}"
PROJECT="icehott-obs-$$"
COMPOSE=(docker compose -p "$PROJECT" -f docker-compose.yml -f docker-compose.observability.yml)
API="http://127.0.0.1:${API_PORT}"

cleanup() { "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true; }
trap cleanup EXIT

fail() { echo "verify-trace: FAIL - $*" >&2; "${COMPOSE[@]}" logs --no-color --tail 60 otel-collector api ai >&2 || true; exit 1; }

wait_for() { # url, attempts
  for _ in $(seq 1 "$2"); do curl -fsS "$1" >/dev/null 2>&1 && return 0; sleep 2; done
  return 1
}

json_field() { sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p" | head -n 1; }

echo "== starting stack"
"${COMPOSE[@]}" up -d --build >/dev/null
wait_for "http://127.0.0.1:${OTEL_HEALTH_PORT}/" 60 || fail "collector did not become healthy"
wait_for "${API}/health" 90 || fail "API did not become healthy"
wait_for "http://127.0.0.1:${AI_PORT}/health" 30 || fail "AI service did not become healthy"

echo "== creating a user and workspace"
EMAIL="obs-$$@example.test"
PASSWORD="Obs-Verify-Pass-$$-2026"
TOKEN=$(curl -fsS -X POST "${API}/api/auth/register" -H 'Content-Type: application/json' \
  -d "{\"email\":\"${EMAIL}\",\"displayName\":\"Obs Verify\",\"password\":\"${PASSWORD}\"}" | json_field accessToken)
[ -n "$TOKEN" ] || fail "no access token"
WORKSPACE=$(curl -fsS -X POST "${API}/api/workspaces" -H "Authorization: Bearer ${TOKEN}" -H 'Content-Type: application/json' \
  -d '{"name":"Observability Verify"}' | json_field id)
[ -n "$WORKSPACE" ] || fail "no workspace id"

chat() { # traceparent
  curl -fsS -o /dev/null -w '%{http_code}' -X POST "${API}/api/workspaces/${WORKSPACE}/conversations/chat" \
    -H "Authorization: Bearer ${TOKEN}" -H 'Content-Type: application/json' -H "traceparent: $1" \
    -d '{"content":"trace verification message"}'
}

TRACE_ID="4bf92f3577b34da6a3ce929d0e0e4736"
echo "== chat with traceparent (trace ${TRACE_ID})"
[ "$(chat "00-${TRACE_ID}-00f067aa0ba902b7-01")" = "200" ] || fail "chat request failed"
trace_services() {
  "${COMPOSE[@]}" logs --no-color otel-collector 2>&1 | awk -v t="$TRACE_ID" '
    /service\.name: Str\(/ { svc = $0; sub(/.*Str\(/, "", svc); sub(/\).*/, "", svc) }
    /Trace ID/ && index($0, t) { seen[svc] = 1 }
    END { for (s in seen) print s }' | sort
}
# Poll (batch export is asynchronous) instead of assuming a fixed delay.
SERVICES=""
for _ in $(seq 1 30); do
  SERVICES=$(trace_services)
  grep -qx "icehott-api" <<<"$SERVICES" && grep -qx "icehott-ai" <<<"$SERVICES" && break
  sleep 3
done
echo "services that exported spans for the trace:"; echo "$SERVICES" | sed 's/^/  - /'
grep -qx "icehott-api" <<<"$SERVICES" || fail "API spans for the trace were not received by the collector"
grep -qx "icehott-ai" <<<"$SERVICES"  || fail "AI spans for the trace were not received by the collector"
echo "== trace context preserved across API -> AI (single trace id in both services)"

echo "== metrics reach the collector (Prometheus endpoint)"
METRICS_URL="http://127.0.0.1:${OTEL_PROMETHEUS_PORT}/metrics"
for _ in $(seq 1 60); do
  BODY=$(curl -fsS "$METRICS_URL" 2>/dev/null || true)
  # Here-strings (not pipes): under pipefail, grep -q closing early would SIGPIPE the writer.
  if grep -q '^icehott_ai_runtime_requests_total' <<<"$BODY" &&
     grep -q '^http_server_request_duration_seconds_count' <<<"$BODY"; then
    break
  fi
  sleep 3
done
grep -q '^icehott_ai_runtime_requests_total' <<<"$BODY" || fail "custom ICEHOTT metrics were not exported"
grep -q '^http_server_request_duration_seconds_count' <<<"$BODY" || fail "ASP.NET Core metrics were not exported"
# Cardinality/redaction spot check: no identifier-shaped labels on custom metrics.
CUSTOM=$(grep '^icehott_' <<<"$BODY" || true)
if grep -Eq '(workspace|user|artifact|run|conversation)_?id' <<<"$CUSTOM"; then
  fail "identifier-shaped label found on a custom metric"
fi

echo "== logs reach the collector"
COLLECTOR_LOG=$("${COMPOSE[@]}" logs --no-color otel-collector 2>&1)
grep -q 'LogRecord' <<<"$COLLECTOR_LOG" || fail "no log records reached the collector"

echo "== stopping the collector: business behaviour must be unaffected"
"${COMPOSE[@]}" stop otel-collector >/dev/null
for i in 1 2 3; do
  [ "$(chat "00-$(openssl rand -hex 16)-$(openssl rand -hex 8)-01")" = "200" ] || fail "chat failed with the collector down"
done
curl -fsS "${API}/health" >/dev/null || fail "API unhealthy with the collector down"
curl -fsS "http://127.0.0.1:${AI_PORT}/health" >/dev/null || fail "AI unhealthy with the collector down"
for service in api ai; do
  state=$("${COMPOSE[@]}" ps --format '{{.State}}' "$service")
  [ "$state" = "running" ] || fail "$service is $state with the collector down"
done

echo "verify-trace: PASS"
