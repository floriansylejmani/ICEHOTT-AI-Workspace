#!/usr/bin/env bash
# Post-deploy smoke check for one ICEHOTT service instance.
#
#   smoke.sh <base-url> <expected-git-sha>
#
# Environment:
#   HOST_HEADER          optional Host header (when AllowedHosts is restricted)
#   SMOKE_REQUIRE_READY  "true" also requires GET /ready = 200 (API role only)
#   SMOKE_ATTEMPTS       health attempts, 2 s apart (default 30)
#
# Fails (non-zero) if the instance is not live or is not running the expected release.
# Prints only status codes and the public release fields; never response secrets.
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
  curl "${curl_args[@]}" -o /dev/null -w '%{http_code}' "$base_url$1" || echo "000"
}

attempts="${SMOKE_ATTEMPTS:-30}"
for ((i = 1; i <= attempts; i++)); do
  code="$(status_of /health)"
  if [[ "$code" == "200" ]]; then
    break
  fi
  if ((i == attempts)); then
    echo "smoke: /health returned $code after $attempts attempts" >&2
    exit 1
  fi
  sleep 2
done
echo "smoke: /health = 200"

release_json="$(curl "${curl_args[@]}" --fail "$base_url/release")"
actual_sha="$(printf '%s' "$release_json" | sed -n 's/.*"gitSha":"\([^"]*\)".*/\1/p')"
service="$(printf '%s' "$release_json" | sed -n 's/.*"service":"\([^"]*\)".*/\1/p')"
environment="$(printf '%s' "$release_json" | sed -n 's/.*"environment":"\([^"]*\)".*/\1/p')"

if [[ "$actual_sha" != "$expected_sha" ]]; then
  echo "smoke: release mismatch: service=$service reports '$actual_sha', expected '$expected_sha'" >&2
  exit 1
fi
echo "smoke: /release ok service=$service environment=$environment gitSha=$actual_sha"

if [[ "${SMOKE_REQUIRE_READY:-false}" == "true" ]]; then
  code="$(status_of /ready)"
  if [[ "$code" != "200" ]]; then
    echo "smoke: /ready returned $code" >&2
    exit 1
  fi
  echo "smoke: /ready = 200"
fi

echo "smoke: PASS"
