#!/usr/bin/env bash
# Smoke-test a published Inventory Tracker executable.
#
#   ./scripts/smoke.sh path/to/PartPilot [timeout-seconds]
#
# Copies the executable ALONE into a fresh temp dir (proving it needs no other files), starts it on a free port with
# a temp database, checks /health, embedded assets and the first-run redirect, then stops it and cleans up.
set -uo pipefail

exe="${1:?usage: smoke.sh <executable> [timeout-seconds]}"
timeout="${2:-90}"
exe="$(cd "$(dirname "$exe")" && pwd)/$(basename "$exe")"

work="$(mktemp -d -t inventory-smoke-XXXXXX)"
pid=""
failures=0

cleanup() {
  [[ -n "$pid" ]] && kill "$pid" 2>/dev/null && wait "$pid" 2>/dev/null
  rm -rf "$work"
}
trap cleanup EXIT

check() { # name ok(0/1) [detail]
  if [[ "$2" == "0" ]]; then echo "  PASS  $1"; else echo "  FAIL  $1 ${3:-}"; failures=$((failures + 1)); fi
}

port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()' 2>/dev/null || echo $((20000 + RANDOM % 20000)))"
cp "$exe" "$work/app" && chmod +x "$work/app"

echo "Smoke test: $exe (port $port)"
( cd "$work" && ./app --db "$work/smoke.db" --port "$port" >"$work/stdout.log" 2>"$work/stderr.log" ) &
pid=$!

base="http://localhost:$port"
healthy=1
for _ in $(seq 1 $((timeout * 2))); do
  kill -0 "$pid" 2>/dev/null || break
  if [[ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$base/health" || true)" == "200" ]]; then healthy=0; break; fi
  sleep 0.5
done
check "starts and /health returns 200" "$healthy"

if [[ "$healthy" == "0" ]]; then
  body="$(curl -s "$base/health")"
  [[ "$body" == *'"db":"ok"'* ]]; check "/health reports db ok" "$?" "($body)"
  [[ "$body" == *'"version":"'* ]]; check "/health reports a version" "$?"

  for asset in /lib/htmx.min.js /lib/pico.min.css /css/site.css /js/site.js; do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$base$asset")"
    [[ "$code" == "200" ]]; check "embedded asset $asset" "$?" "(status $code)"
  done

  # The home page must render (200) or, once authentication exists, send a fresh install to first-run setup / login.
  headers="$(curl -s -o /dev/null -D - --max-time 5 "$base/" | tr -d '\r')"
  status="$(echo "$headers" | awk 'NR==1 {print $2}')"
  loc="$(echo "$headers" | awk 'tolower($1)=="location:" {print $2}')"
  ok=1
  if [[ "$status" == "200" ]]; then ok=0
  elif [[ "$status" =~ ^30[12378]$ && "$loc" =~ /Account/(Setup|Login) ]]; then ok=0; fi
  check "home page renders or redirects to setup/login" "$ok" "(status $status, Location: $loc)"
  [[ -f "$work/smoke.db" ]]; check "database file created" "$?"
else
  tail -n 30 "$work/stdout.log" "$work/stderr.log" 2>/dev/null
fi

if [[ "$failures" -gt 0 ]]; then echo "Smoke test FAILED ($failures check(s))"; exit 1; fi
echo "Smoke test passed"
