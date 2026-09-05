#!/usr/bin/env bash
#
# Proves the production stack's exit criterion: `docker compose up -d` brings everything up
# behind Traefik from a clean checkout, and the console and the fleet API are reachable through
# the proxy and *only* through the proxy.
#
# Builds real images, so the first run takes a few minutes. Everything it creates - project,
# volumes, generated .env, self-signed certificate - is removed on exit, including on failure.
#
#   bash scripts/compose-prod-smoke.sh
#
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMPOSE_FILE="$ROOT_DIR/infra/docker-compose.prod.yml"
PROJECT="printo_prod_smoke_$$"

# High ports, so the smoke test never fights whatever is already on 80/443.
HTTP_PORT="${TRAEFIK_HTTP_PORT:-18080}"
HTTPS_PORT="${TRAEFIK_HTTPS_PORT:-18443}"

ENV_FILE="$(mktemp)"
CERT_DIR="$ROOT_DIR/infra/traefik/certs"
DYNAMIC_DIR="$ROOT_DIR/infra/traefik/dynamic"
CERT_MADE=0
TLS_STORE_MADE=0

compose() {
  docker compose -p "$PROJECT" --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"
}

cleanup() {
  local status=$?
  if [ "$status" -ne 0 ]; then
    echo "--- smoke failed; recent service logs ---" >&2
    compose logs --tail 40 traefik api web 2>&1 | tail -80 >&2 || true
  fi
  compose down -v --remove-orphans >/dev/null 2>&1 || true
  rm -f "$ENV_FILE"
  if [ "$CERT_MADE" -eq 1 ]; then
    rm -f "$CERT_DIR/printo.crt" "$CERT_DIR/printo.key"
  fi
  if [ "$TLS_STORE_MADE" -eq 1 ]; then
    rm -f "$DYNAMIC_DIR/tls.yml"
  fi
  exit "$status"
}
trap cleanup EXIT

# Hex, not base64: the password is interpolated into a `postgres://user:pass@host` URL by the
# compose file, and base64 emits `/`, `+` and `=`.
secret() {
  openssl rand -hex 32 | tr -d '\n'
}

echo "==> writing a throwaway environment"
cat > "$ENV_FILE" <<EOF
NODE_ENV=production
POSTGRES_DB=printo
POSTGRES_USER=printo
POSTGRES_PASSWORD=$(secret)
JWT_ACCESS_SECRET=$(secret)
JWT_REFRESH_SECRET=$(secret)
JWT_ACCESS_TTL=15m
JWT_REFRESH_TTL=7d
AUTH_HASH_STRATEGY=auto
EXTAUTH_BASE_URL=
EXTAUTH_API_KEY=
BOOTSTRAP_ADMIN_TOKEN=
TRAEFIK_HTTP_PORT=$HTTP_PORT
TRAEFIK_HTTPS_PORT=$HTTPS_PORT
WORKER_SCANNER=auto
# Never a real printer from a smoke test.
WORKER_DISPATCH_PROVIDER_MODE=mock
WORKER_POLL_INTERVAL_MS=60000
WORKER_CLASSIFIER=heuristic
# The rasterizing profile takes many minutes to build and nothing here needs it.
VISION_BUILD_PROFILE=minimal
RETENTION_INTERVAL_HOURS=24
EOF

echo "==> building and starting the stack, with no certificate configured"
compose up -d --build

wait_for() {
  local what="$1" url="$2" attempts="${3:-90}"
  for ((i = 1; i <= attempts; i++)); do
    if curl -ksSf -o /dev/null "$url" 2>/dev/null; then
      echo "    $what is up"
      return 0
    fi
    sleep 2
  done
  echo "!!! $what never became reachable at $url" >&2
  return 1
}

BASE="https://127.0.0.1:$HTTPS_PORT"

# Bodies are captured before matching, never piped into `grep -q`. Under `pipefail` that
# pipeline fails whenever the body is big enough that grep exits on its first match and curl
# then dies writing to a closed pipe — which made the 150 KB admin page fail a check the tiny
# /health response passed.
expect_body() {
  local what="$1" url="$2" needle="$3" body
  body="$(curl -ksS "$url")"
  case "$body" in
    *"$needle"*) echo "    $what" ;;
    *) echo "!!! $what: $url did not contain '$needle'" >&2; return 1 ;;
  esac
}

echo "==> the console is served through Traefik"
wait_for "web" "$BASE/health"
expect_body "the web service answers" "$BASE/health" '"service":"web"'

echo "==> the fleet API is served through Traefik under /api"
wait_for "api" "$BASE/api/health"
expect_body "the api service answers" "$BASE/api/health" '"status"'

echo "==> the admin console renders, with the fleet tab"
expect_body "the fleet tab is present" "$BASE/admin/config" 'data-tab="fleet"'

echo "==> plain HTTP redirects to HTTPS"
redirect="$(curl -sS -o /dev/null -w '%{http_code} %{redirect_url}' "http://127.0.0.1:$HTTP_PORT/health")"
case "$redirect" in
  30*https://*) echo "    $redirect" ;;
  *) echo "!!! expected a redirect to https, got: $redirect" >&2; exit 1 ;;
esac

echo "==> nothing but Traefik publishes a port"
# Read the structured output rather than a Go template: the `Publishers` template renders as an
# opaque struct dump whose shape varies by Docker version, and a check that silently matched
# nothing would pass while the database sat exposed on the LAN.
published="$(compose ps --format json | python -c '
import json, sys

services = set()
for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    payload = json.loads(line)
    for entry in payload if isinstance(payload, list) else [payload]:
        for publisher in entry.get("Publishers") or []:
            if publisher.get("PublishedPort"):
                services.add(entry["Service"])

print(",".join(sorted(services)))
')"

if [ "$published" != "traefik" ]; then
  echo "!!! expected only traefik to publish ports, got: ${published:-none}" >&2
  exit 1
fi
echo "    only traefik: $published"

echo "==> the agent API refuses an unenrolled machine through the proxy"
code="$(curl -ksS -o /dev/null -w '%{http_code}' "$BASE/api/agents/me")"
[ "$code" = "401" ] || { echo "!!! expected 401 from /api/agents/me, got $code" >&2; exit 1; }

echo "==> migrations ran: the fleet schema is present"
tables="$(compose exec -T db psql -U printo -d printo -tAc \
  "SELECT COUNT(*) FROM information_schema.tables WHERE table_name IN ('agents','rule_bundles','fallback_events')")"
tables="$(printf '%s' "$tables" | tr -d '[:space:]')"
[ "$tables" = "3" ] || { echo "!!! expected 3 fleet tables, found $tables" >&2; exit 1; }
echo "    all three fleet tables exist"

# Everything above ran against the certificate Traefik generates for itself, which is what a
# clean checkout gets. Now the real path: a certificate on disk and the TLS store switched on.
echo "==> the certificate store loads a real certificate"
if [ ! -f "$CERT_DIR/printo.crt" ]; then
  # The doubled slash in the subject is a Git-Bash-on-Windows escape: MSYS would otherwise
  # rewrite the leading `/` into a drive path and openssl would reject the subject. It collapses
  # back to one slash on the way through and means nothing anywhere else. Setting
  # MSYS_NO_PATHCONV instead would fix the subject and break `-keyout`, which does need
  # translating.
  openssl req -x509 -newkey rsa:2048 -nodes -days 2 \
    -subj "//CN=printo.smoke" \
    -keyout "$CERT_DIR/printo.key" -out "$CERT_DIR/printo.crt" >/dev/null 2>&1
  CERT_MADE=1
fi

if [ ! -f "$DYNAMIC_DIR/tls.yml" ]; then
  cp "$DYNAMIC_DIR/tls.yml.example" "$DYNAMIC_DIR/tls.yml"
  TLS_STORE_MADE=1
fi

# Restarting rather than relying on the watch. Traefik does reload dynamic files in place, but
# the watch is driven by inotify, which does not fire reliably for a file created on a bind
# mount from a Windows or macOS host - the very setup an operator is most likely to try this on.
# A restart is two seconds and works everywhere.
compose restart traefik >/dev/null

subject=""
for _ in $(seq 1 20); do
  subject="$(echo | openssl s_client -connect "127.0.0.1:$HTTPS_PORT" 2>/dev/null \
    | openssl x509 -noout -subject 2>/dev/null || true)"
  case "$subject" in
    *printo.smoke*) break ;;
  esac
  sleep 2
done

case "$subject" in
  *printo.smoke*) echo "    serving the configured certificate: $subject" ;;
  *) echo "!!! the TLS store never picked up the certificate (subject: ${subject:-none})" >&2; exit 1 ;;
esac

echo "==> the site still works with the store enabled"
curl -ksSf -o /dev/null "$BASE/health"
echo "    still serving"

echo
echo "production compose smoke passed"
