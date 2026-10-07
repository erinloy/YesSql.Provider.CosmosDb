#!/usr/bin/env bash
# Builds samples/OrchardSmokeTest against an Orchard Core version, starts it against a running Cosmos DB
# emulator (http://localhost:8081) and checks that Orchard sets up the tenant and serves the site.
#
# usage: scripts/smoke-sample.sh [orchard-core-version] [port]
#   scripts/smoke-sample.sh 3.0.1                    # YesSql 5.4.7
#   scripts/smoke-sample.sh 4.0.0-preview-19175      # YesSql 6.0.0
#
# Environment:
#   SMOKE_PARTITION=PerStore   use the PerStore partition strategy (default PerTable)
#   SMOKE_CONTENT=1            also run the content checks (samples/OrchardSmokeTest/ContentChecks.cs): create, publish,
#                              edit, unpublish and remove content items, ordered and paged queries, concurrent creates
#                              and a cancelled request, all through Orchard's content manager
set -euo pipefail

version="${1:-3.0.1}"
port="${2:-5070}"
root="$(cd "$(dirname "$0")/.." && pwd)"
sample="$root/samples/OrchardSmokeTest"
# A fresh database per run, so Orchard's tenant state and Cosmos always agree.
database="orchard_smoke_$(echo "$version" | tr -c 'a-zA-Z0-9' _)_$(date +%s)"
contentroot="$(cygpath -w "$sample" 2>/dev/null || echo "$sample")"

rm -rf "${sample:?}/App_Data"
mkdir -p "$sample/App_Data"
log="$sample/App_Data/smoke.log"

dotnet build "$sample" -c Release -p:OrchardCoreVersion="$version" --nologo -v q

SmokeTest__ContentChecks="$([ "${SMOKE_CONTENT:-0}" = "1" ] && echo true || echo false)" Cosmos__PartitionStrategy="${SMOKE_PARTITION:-PerTable}" ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS="http://localhost:$port" \
ASPNETCORE_CONTENTROOT="$contentroot" \
Cosmos__Database="$database" \
  dotnet "$sample/bin/Release/net10.0/OrchardSmokeTest.dll" > "$log" 2>&1 &
pid=$!
trap 'kill "$pid" 2>/dev/null || true' EXIT

fail() {
  echo "FAIL: $1" >&2
  echo "--- application log ---" >&2
  cat "$log" >&2
  exit 1
}

# The first request triggers AutoSetup, which can take a minute or two against the emulator.
for _ in $(seq 1 60); do
  kill -0 "$pid" 2>/dev/null || fail "application exited during startup"
  [ "$(curl -s -o /dev/null -w '%{http_code}' -m 60 "http://localhost:$port/" || true)" = "200" ] && break
  sleep 5
done

for path in / /Login /admin; do
  page="$sample/App_Data/page.html"
  code="$(curl -s -o "$page" -w '%{http_code}' -m 60 -L "http://localhost:$port$path" || true)"
  [ "$code" = "200" ] || fail "GET $path returned $code"
  grep -q "Cosmos Smoke Test" "$page" || fail "GET $path did not return the Cosmos Smoke Test site"
  echo "GET $path -> $code"
done

if [ "${SMOKE_CONTENT:-0}" = "1" ]; then
  result="$sample/App_Data/content.json"
  code="$(curl -s -o "$result" -w '%{http_code}' -m 600 "http://localhost:$port/_smoke/content" || true)"
  # One line per check: ok/FAIL, name, detail.
  python3 - "$result" <<'PY' || true
import json, sys
with open(sys.argv[1], encoding="utf-8") as f:
    checks = json.load(f)
for c in checks:
    print(("ok   " if c["ok"] else "FAIL ") + c["name"] + ("" if c["ok"] else "  -> " + c["detail"]))
print(f"{sum(c['ok'] for c in checks)} of {len(checks)} content checks passed")
PY
  [ "$code" = "200" ] || fail "the content checks returned $code"
fi

if grep -q -E '^(fail|crit):' "$log"; then
  fail "the application logged errors"
fi

echo "OK: Orchard Core $version set up and served from Cosmos database $database"
