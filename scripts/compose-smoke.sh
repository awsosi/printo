#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMPOSE_FILE="$ROOT_DIR/infra/docker-compose.yml"
COMPOSE_PROJECT_NAME="printo_smoke_${RANDOM}_$$"

WEB_PORT="${WEB_PORT:-13000}"
API_PORT="${API_PORT:-14000}"
WORKER_PORT="${WORKER_PORT:-15000}"
DB_PORT="${DB_PORT:-15432}"
REDIS_PORT="${REDIS_PORT:-16379}"

export WEB_PORT API_PORT WORKER_PORT DB_PORT REDIS_PORT

compose() {
  docker compose -p "$COMPOSE_PROJECT_NAME" -f "$COMPOSE_FILE" "$@"
}

cleanup() {
  compose down -v --remove-orphans >/dev/null 2>&1 || true
}

trap cleanup EXIT

wait_for_http() {
  local url="$1"
  local attempts="${2:-60}"

  for ((i=1; i<=attempts; i++)); do
    if curl -fsS "$url" >/dev/null 2>&1; then
      return 0
    fi
    sleep 2
  done

  echo "Timed out waiting for $url" >&2
  return 1
}

request_json() {
  local method="$1"
  local url="$2"
  local body="${3:-}"
  local auth="${4:-}"
  local attempts=20

  for ((i=1; i<=attempts; i++)); do
    local headers=(-H 'accept: application/json')
    local response_file
    response_file="$(mktemp)"
    if [[ -n "$auth" ]]; then
      headers+=(-H "authorization: Bearer $auth")
    fi

    if [[ -n "$body" ]]; then
      headers+=(-H 'content-type: application/json')
      status="$(curl -sS -o "$response_file" -w '%{http_code}' -X "$method" "$url" "${headers[@]}" -d "$body")"
      if [[ "$status" =~ ^2[0-9][0-9]$ ]]; then
        response="$(cat "$response_file")"
        rm -f "$response_file"
        printf '%s' "$response"
        return 0
      fi
    else
      status="$(curl -sS -o "$response_file" -w '%{http_code}' -X "$method" "$url" "${headers[@]}")"
      if [[ "$status" =~ ^2[0-9][0-9]$ ]]; then
        response="$(cat "$response_file")"
        rm -f "$response_file"
        printf '%s' "$response"
        return 0
      fi
    fi

    echo "Request attempt $i failed: $method $url (status=$status)" >&2
    if [[ -s "$response_file" ]]; then
      cat "$response_file" >&2
      echo >&2
    fi
    rm -f "$response_file"

    sleep 1
  done

  echo "Request failed after retries: $method $url" >&2
  return 1
}

cd "$ROOT_DIR"

compose up -d db redis api
wait_for_http "http://127.0.0.1:${API_PORT}/health"

compose up -d worker web
wait_for_http "http://127.0.0.1:${WORKER_PORT}/health"
wait_for_http "http://127.0.0.1:${WEB_PORT}/health"

ADMIN_USER="smoke_admin_$(date +%s)"
ADMIN_PASS="AdminPass123!"

request_json POST "http://127.0.0.1:${API_PORT}/auth/register" \
  "{\"username\":\"$ADMIN_USER\",\"password\":\"$ADMIN_PASS\",\"roles\":[\"ADMIN\"]}" >/dev/null

LOGIN_RESPONSE="$(request_json POST "http://127.0.0.1:${API_PORT}/auth/login" \
  "{\"username\":\"$ADMIN_USER\",\"password\":\"$ADMIN_PASS\"}")"

ACCESS_TOKEN="$(printf '%s' "$LOGIN_RESPONSE" | node -pe "JSON.parse(require('fs').readFileSync(0, 'utf8')).accessToken")"

request_json POST "http://127.0.0.1:${API_PORT}/admin/config/printers" \
  '{"name":"smoke-a4","type":"A4","targetUri":"ipp://a4.local/queue"}' \
  "$ACCESS_TOKEN" >/dev/null

request_json POST "http://127.0.0.1:${API_PORT}/admin/config/printers" \
  '{"name":"smoke-thermal","type":"THERMAL","targetUri":"socket://thermal.local:9100"}' \
  "$ACCESS_TOKEN" >/dev/null

A4_ID="$(request_json GET "http://127.0.0.1:${API_PORT}/admin/config/printers" '' "$ACCESS_TOKEN" \
  | node -pe "const data=JSON.parse(require('fs').readFileSync(0,'utf8'));(data.find((row)=>row.type==='A4')||{}).id||''")"

request_json POST "http://127.0.0.1:${API_PORT}/admin/config/smb-sources" \
  '{"path":"/app/fixtures/intake","domainUsername":"serviceuser@example.local","secretRef":"secret/smoke"}' \
  "$ACCESS_TOKEN" >/dev/null

request_json POST "http://127.0.0.1:${API_PORT}/admin/config/filename-masks" \
  '{"pattern":"invoice","isRegex":false}' \
  "$ACCESS_TOKEN" >/dev/null

request_json POST "http://127.0.0.1:${API_PORT}/admin/config/routing-profiles" \
  "{\"name\":\"smoke-routing\",\"thermalLabelPatterns\":[\"label\"],\"fallbackPrinterId\":\"$A4_ID\"}" \
  "$ACCESS_TOKEN" >/dev/null

request_json PUT "http://127.0.0.1:${API_PORT}/admin/config/ocr/global" \
  '{"provider":"mock","config":{"thermalKeyword":"label"}}' \
  "$ACCESS_TOKEN" >/dev/null

RUN_OUTPUT="$(request_json POST "http://127.0.0.1:${WORKER_PORT}/pipeline/run-once")"
FILES_PROCESSED="$(printf '%s' "$RUN_OUTPUT" | node -pe "JSON.parse(require('fs').readFileSync(0,'utf8')).summary.filesProcessed")"

if [[ "$FILES_PROCESSED" -lt 1 ]]; then
  echo "Expected filesProcessed >= 1, got $FILES_PROCESSED" >&2
  exit 1
fi

PROCESSED_COUNT="$(compose exec -T db psql -U printo -d printo -tAc "SELECT COUNT(*) FROM processed_files;")"
JOBS_COUNT="$(compose exec -T db psql -U printo -d printo -tAc "SELECT COUNT(*) FROM print_jobs;")"
PAGES_COUNT="$(compose exec -T db psql -U printo -d printo -tAc "SELECT COUNT(*) FROM print_job_pages;")"

if [[ "$PROCESSED_COUNT" -lt 1 || "$JOBS_COUNT" -lt 1 || "$PAGES_COUNT" -lt 1 ]]; then
  echo "Smoke verification failed: processed=$PROCESSED_COUNT jobs=$JOBS_COUNT pages=$PAGES_COUNT" >&2
  exit 1
fi

echo "compose smoke ok: processed=$PROCESSED_COUNT jobs=$JOBS_COUNT pages=$PAGES_COUNT"
