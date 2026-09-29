#!/usr/bin/env bash
# Post-deploy smoke check for one ICEHOTT service instance.
#
#   smoke.sh <base-url> <expected-git-sha>
#
# Environment:
#   HOST_HEADER          optional Host header (when AllowedHosts is restricted)
#   SMOKE_REQUIRE_READY  "true" also requires GET /ready = 200 (API role only)
#   SMOKE_ATTEMPTS       polling attempts (default 90)
#   SMOKE_INTERVAL       seconds between attempts (default 2)
#
# During a rolling deploy the previous release keeps answering until the new one is
# healthy, so the check polls until the instance is live AND reports the expected release.
# It fails (non-zero) if that does not happen within the window. It prints only status codes
# and the public release fields, never response bodies.
set -euo pipefail

base_url="${1:?usage: smoke.sh <base-url> <expected-git-sha>}"
expected_sha="${2:?usage: smoke.sh <base-url> <expected-git-sha>}"
base_url="${base_url%/}"

if ! [[ "$expected_sha" =~ ^[0-9a-f]{7,64}$ ]]; then
  echo "smoke: expected git SHA must be 7-64 lowercase hex characters" >&2
  exit 2
fi

curl_args=(-sS --max-time 10)
if [[ -n "${HOST_HEADER:-}" ]]; then
  curl_args+=(-H "Host: ${HOST_HEADER}")
fi

status_of() {
  local code
  code="$(curl "${curl_args[@]}" -o /dev/null -w '%{http_code}' "$base_url$1" 2>/dev/null)" || true
  echo "${code:-000}"
}

field_of() {
  # $1 = json, $2 = field name (flat string fields only)
  printf '%s' "$1" | sed -n "s/.*\"$2\":\"\([^\"]*\)\".*/\1/p"
}

attempts="${SMOKE_ATTEMPTS:-90}"
interval="${SMOKE_INTERVAL:-2}"
last="not live"
service=""
environment=""
actual_sha=""

for ((i = 1; i <= attempts; i++)); do
  if [[ "$(status_of /health)" == "200" ]]; then
    release_json="$(curl "${curl_args[@]}" --fail "$base_url/release" 2>/dev/null)" || release_json=""
    actual_sha="$(field_of "$release_json" gitSha)"
    service="$(field_of "$release_json" service)"
    environment="$(field_of "$release_json" environment)"
    if [[ "$actual_sha" == "$expected_sha" ]]; then
      last="ok"
      break
    fi
    last="live but reporting release '${actual_sha:-unavailable}'"
  else
    last="not live"
  fi
  ((i < attempts)) && sleep "$interval"
done

if [[ "$last" != "ok" ]]; then
  echo "smoke: expected release $expected_sha not serving after $attempts attempts ($last)" >&2
  exit 1
fi
echo "smoke: /health = 200"
echo "smoke: /release ok service=$service environment=$environment gitSha=$actual_sha"

if [[ "${SMOKE_REQUIRE_READY:-false}" == "true" ]]; then
  ready="000"
  for ((i = 1; i <= attempts; i++)); do
    ready="$(status_of /ready)"
    [[ "$ready" == "200" ]] && break
    ((i < attempts)) && sleep "$interval"
  done
  if [[ "$ready" != "200" ]]; then
    echo "smoke: /ready returned $ready" >&2
    exit 1
  fi
  echo "smoke: /ready = 200"
fi

echo "smoke: PASS"
