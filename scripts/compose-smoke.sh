#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMPOSE_FILE="$ROOT_DIR/infra/docker-compose.yml"

cleanup() {
  docker compose -f "$COMPOSE_FILE" down -v --remove-orphans >/dev/null 2>&1 || true
}

trap cleanup EXIT

wait_for_http() {
  local url="$1"
  local attempts=60
  for ((i=1; i<=attempts; i++)); do
    if curl -fsS "$url" >/dev/null 2>&1; then
      return 0
    fi
    sleep 2
  done

  echo "Timed out waiting for $url" >&2
  return 1
}

cd "$ROOT_DIR"

docker compose -f "$COMPOSE_FILE" up -d db redis api
wait_for_http "http://127.0.0.1:4000/health"

docker compose -f "$COMPOSE_FILE" up -d worker web
wait_for_http "http://127.0.0.1:5000/health"
wait_for_http "http://127.0.0.1:3000/health"

ADMIN_USER="smoke_admin_$(date +%s)"
ADMIN_PASS="AdminPass123!"

curl -fsS -X POST http://127.0.0.1:4000/auth/register \
  -H 'content-type: application/json' \
  -d "{\"username\":\"$ADMIN_USER\",\"password\":\"$ADMIN_PASS\",\"roles\":[\"ADMIN\"]}" >/dev/null

LOGIN_RESPONSE="$(curl -fsS -X POST http://127.0.0.1:4000/auth/login \
  -H 'content-type: application/json' \
  -d "{\"username\":\"$ADMIN_USER\",\"password\":\"$ADMIN_PASS\"}")"

ACCESS_TOKEN="$(printf '%s' "$LOGIN_RESPONSE" | node -pe "JSON.parse(require('fs').readFileSync(0, 'utf8')).accessToken")"

curl -fsS -X POST http://127.0.0.1:4000/admin/config/printers \
  -H "authorization: Bearer $ACCESS_TOKEN" \
  -H 'content-type: application/json' \
  -d '{"name":"smoke-a4","type":"A4","targetUri":"ipp://a4.local/queue"}' >/dev/null

curl -fsS -X POST http://127.0.0.1:4000/admin/config/printers \
  -H "authorization: Bearer $ACCESS_TOKEN" \
  -H 'content-type: application/json' \
  -d '{"name":"smoke-thermal","type":"THERMAL","targetUri":"socket://thermal.local:9100"}' >/dev/null

A4_ID="$(curl -fsS http://127.0.0.1:4000/admin/config/printers -H "authorization: Bearer $ACCESS_TOKEN" | node -pe "const data=JSON.parse(require('fs').readFileSync(0,'utf8'));(data.find((row)=>row.type==='A4')||{}).id||''")"

curl -fsS -X POST http://127.0.0.1:4000/admin/config/smb-sources \
  -H "authorization: Bearer $ACCESS_TOKEN" \
  -H 'content-type: application/json' \
  -d '{"path":"/app/fixtures/intake","domainUsername":"EXAMPLE\\\\serviceuser","secretRef":"secret/smoke"}' >/dev/null

curl -fsS -X POST http://127.0.0.1:4000/admin/config/filename-masks \
  -H "authorization: Bearer $ACCESS_TOKEN" \
  -H 'content-type: application/json' \
  -d '{"pattern":"invoice","isRegex":false}' >/dev/null

curl -fsS -X POST http://127.0.0.1:4000/admin/config/routing-profiles \
  -H "authorization: Bearer $ACCESS_TOKEN" \
  -H 'content-type: application/json' \
  -d "{\"name\":\"smoke-routing\",\"thermalLabelPatterns\":[\"label\"],\"fallbackPrinterId\":\"$A4_ID\"}" >/dev/null

curl -fsS -X PUT http://127.0.0.1:4000/admin/config/ocr/global \
  -H "authorization: Bearer $ACCESS_TOKEN" \
  -H 'content-type: application/json' \
  -d '{"provider":"mock","config":{"thermalKeyword":"label"}}' >/dev/null

RUN_OUTPUT="$(curl -fsS -X POST http://127.0.0.1:5000/pipeline/run-once)"
FILES_PROCESSED="$(printf '%s' "$RUN_OUTPUT" | node -pe "JSON.parse(require('fs').readFileSync(0,'utf8')).summary.filesProcessed")"

if [[ "$FILES_PROCESSED" -lt 1 ]]; then
  echo "Expected filesProcessed >= 1, got $FILES_PROCESSED" >&2
  exit 1
fi

PROCESSED_COUNT="$(docker compose -f "$COMPOSE_FILE" exec -T db psql -U printo -d printo -tAc "SELECT COUNT(*) FROM processed_files;")"
JOBS_COUNT="$(docker compose -f "$COMPOSE_FILE" exec -T db psql -U printo -d printo -tAc "SELECT COUNT(*) FROM print_jobs;")"
PAGES_COUNT="$(docker compose -f "$COMPOSE_FILE" exec -T db psql -U printo -d printo -tAc "SELECT COUNT(*) FROM print_job_pages;")"

if [[ "$PROCESSED_COUNT" -lt 1 || "$JOBS_COUNT" -lt 1 || "$PAGES_COUNT" -lt 1 ]]; then
  echo "Smoke verification failed: processed=$PROCESSED_COUNT jobs=$JOBS_COUNT pages=$PAGES_COUNT" >&2
  exit 1
fi

echo "compose smoke ok: processed=$PROCESSED_COUNT jobs=$JOBS_COUNT pages=$PAGES_COUNT"