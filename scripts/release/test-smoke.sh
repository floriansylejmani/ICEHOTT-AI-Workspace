#!/usr/bin/env bash
# Behavioural test for smoke.sh using a throwaway local HTTP stub (needs python3 + curl).
# Proves the smoke check tolerates a rolling deploy (old release answering first) and still
# fails when the expected release never arrives or the service is not live.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
old_sha="1111111111111111111111111111111111111111"
new_sha="2222222222222222222222222222222222222222"
stub_pid=""
python_cmd="$(command -v python3 || command -v python)"

start_stub() {
  # $1 = number of /release responses that still report the old SHA before switching
  local flips="$1"
  "$python_cmd" - "$flips" "$old_sha" "$new_sha" >"$workdir/port" <<'PY' &
import http.server, sys, threading
flips, old, new = int(sys.argv[1]), sys.argv[2], sys.argv[3]
state = {"n": 0}
class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def do_GET(self):
        if self.path == "/health":
            self.send_response(200); self.end_headers(); self.wfile.write(b"Healthy"); return
        if self.path == "/release":
            state["n"] += 1
            sha = old if state["n"] <= flips else new
            body = ('{"service":"icehott-api","role":"Api","environment":"staging",'
                    '"gitSha":"%s","version":"1.0.0"}' % sha).encode()
            self.send_response(200); self.send_header("Content-Type", "application/json")
            self.end_headers(); self.wfile.write(body); return
        self.send_response(404); self.end_headers()
srv = http.server.HTTPServer(("127.0.0.1", 0), H)
print(srv.server_address[1], flush=True)
srv.serve_forever()
PY
  stub_pid=$!
  for _ in $(seq 1 50); do [[ -s "$workdir/port" ]] && break; sleep 0.1; done
  port="$(cat "$workdir/port")"
}

stop_stub() {
  [[ -n "$stub_pid" ]] && kill "$stub_pid" 2>/dev/null || true
  stub_pid=""
  : >"$workdir/port"
}

workdir="$(mktemp -d)"
trap 'stop_stub; rm -rf "$workdir"' EXIT

fail() { echo "test-smoke: FAIL - $*" >&2; exit 1; }

echo "== rolling deploy: old release answers first, then the new one"
start_stub 3
SMOKE_ATTEMPTS=10 SMOKE_INTERVAL=0 "$here/smoke.sh" "http://127.0.0.1:$port" "$new_sha" >/dev/null \
  || fail "smoke did not wait for the new release during a rolling deploy"
stop_stub

echo "== wrong release forever"
start_stub 1000
if SMOKE_ATTEMPTS=4 SMOKE_INTERVAL=0 "$here/smoke.sh" "http://127.0.0.1:$port" "$new_sha" >/dev/null 2>&1; then
  fail "smoke passed although the expected release never arrived"
fi
stop_stub

echo "== correct release immediately"
start_stub 0
SMOKE_ATTEMPTS=2 SMOKE_INTERVAL=0 "$here/smoke.sh" "http://127.0.0.1:$port" "$new_sha" >/dev/null \
  || fail "smoke failed for the correct release"
stop_stub

echo "== service not live"
if SMOKE_ATTEMPTS=2 SMOKE_INTERVAL=0 "$here/smoke.sh" "http://127.0.0.1:9" "$new_sha" >/dev/null 2>&1; then
  fail "smoke passed against a dead endpoint"
fi

echo "== malformed expected SHA is refused"
if "$here/smoke.sh" "http://127.0.0.1:9" "latest" >/dev/null 2>&1; then
  fail "smoke accepted a mutable reference"
fi

echo "test-smoke: PASS"
