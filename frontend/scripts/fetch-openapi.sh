#!/usr/bin/env bash
# Fetch GET /openapi/v1.json after first-run setup + login.
# Token is read from the API console log, never passed on the command line.
set -euo pipefail

API="${AIRSIDE_API_URL:-http://127.0.0.1:5109}"
OUT="${1:-lib/api/openapi.json}"
COOKIE="${TMPDIR:-/tmp}/airside-openapi-cookies.txt"
LOG="${AIRSIDE_API_LOG:-}"

if [[ -z "$LOG" ]]; then
  echo "Set AIRSIDE_API_LOG to the API stdout file that printed the setup token." >&2
  exit 1
fi

TOKEN="$(python3 - "$LOG" <<'PY'
import re, sys
text = open(sys.argv[1], errors="replace").read()
# Token is the only long line inside the setup box
m = re.search(r"one-time setup token:.*?│\s+([A-Za-z0-9_-]{20,})\s+│", text, re.S)
if not m:
    raise SystemExit("setup token not found in API log")
print(m.group(1))
PY
)"

curl -sS "$API/api/v1/setup/status" >/dev/null

# Complete setup if still open. Ignore failure if already completed.
curl -sS -c "$COOKIE" -H 'Content-Type: application/json' \
  -d "$(python3 -c "import json,os; print(json.dumps({
    'setupToken': os.environ['TOKEN'],
    'email': os.environ.get('AIRSIDE_DEV_EMAIL','admin@airside.local'),
    'password': os.environ.get('AIRSIDE_DEV_PASSWORD','AirsideDev1!'),
    'displayName': 'Admin',
    'instanceName': 'dev',
  }))")" \
  "$API/api/v1/setup/complete" >/dev/null || true

TOKEN="$TOKEN" AIRSIDE_DEV_EMAIL="${AIRSIDE_DEV_EMAIL:-admin@airside.local}" \
AIRSIDE_DEV_PASSWORD="${AIRSIDE_DEV_PASSWORD:-AirsideDev1!}" \
python3 - "$API" "$COOKIE" <<'PY'
import json, os, urllib.request
api, cookie = os.sys.argv[1], os.sys.argv[2]
req = urllib.request.Request(
    api + "/api/v1/auth/login",
    data=json.dumps({
        "email": os.environ["AIRSIDE_DEV_EMAIL"],
        "password": os.environ["AIRSIDE_DEV_PASSWORD"],
    }).encode(),
    headers={"Content-Type": "application/json"},
    method="POST",
)
# reuse cookie jar via curl below
PY

curl -sS -c "$COOKIE" -b "$COOKIE" -H 'Content-Type: application/json' \
  -d "$(python3 -c "import json,os; print(json.dumps({
    'email': os.environ.get('AIRSIDE_DEV_EMAIL','admin@airside.local'),
    'password': os.environ.get('AIRSIDE_DEV_PASSWORD','AirsideDev1!'),
  }))")" \
  "$API/api/v1/auth/login" >/dev/null

mkdir -p "$(dirname "$OUT")"
curl -sS -b "$COOKIE" -o "$OUT" -w "wrote %{size_download} bytes %{http_code}\n" \
  "$API/openapi/v1.json"
rm -f "$COOKIE"
