# printo

Docker-compose-based end-to-end web application for PDF intake, OCR/visual routing, and split printing (A4 + thermal).

## Stack

- `apps/api` — TypeScript REST API (AAA + config)
- `apps/web` — TypeScript admin/user web UI
- `apps/worker` — TypeScript background pipeline (scan → OCR → route → dispatch)
- `db` — PostgreSQL
- `redis` — queue backbone placeholder

## Requirements covered

- Local users with CRUD
- Remote auth via `EXTAUTH_API.md` contract (`/RFM_Auth`)
- RBAC roles: `USER`, `ADMIN`
- Audit logging for auth and configuration changes
- SMB source config + filename masks
- A4/thermal printer config + per-user assignment + routing profile
- OCR global config + per-user override
- i18n JSON with `en-US` fallback
- Theme mode: `system` / `light` / `dark` with user override

## Quickstart (local)

```bash
npm install
make lint
make test
make typecheck
make build
make e2e
```

## First-time admin setup

Open `http://127.0.0.1:${WEB_PORT:-3000}` and use the **Initial admin setup** panel.

- The first admin can be created only once.
- After bootstrap, public registration cannot create `ADMIN` users.
- Optional hardening: set `BOOTSTRAP_ADMIN_TOKEN` to require a one-time token during bootstrap.

## Run with docker compose

Compose file location: `infra/docker-compose.yml`

```bash
# start
docker compose -f infra/docker-compose.yml up -d

# web:    http://127.0.0.1:${WEB_PORT:-3000}
# api:    http://127.0.0.1:${API_PORT:-4000}/health
# worker: http://127.0.0.1:${WORKER_PORT:-5000}/health

# stop + cleanup
docker compose -f infra/docker-compose.yml down -v --remove-orphans
```

Equivalent shortcuts:

```bash
make up
make down
```

Host ports are configurable in `.env.example`:
- `WEB_PORT`, `API_PORT`, `WORKER_PORT`, `DB_PORT`, `REDIS_PORT`

Worker adapter knobs (also in `.env.example`):
- `WORKER_SCANNER=auto|filesystem|smb|static`
  - `auto`: local filesystem for non-UNC paths, `smbclient` scanner for UNC paths (`\\server\\share\\...`)
- `WORKER_DISPATCH_PROVIDER_MODE=mock|auto|windows|socket|ipp`
  - `mock` is deterministic/default for local + CI
  - `auto` infers provider per printer from `targetUri` (`\\server\printer`, `smb://server/printer`, `socket://`, `ipp://`, `http(s)://`)
- `WORKER_PRINTER_PROVIDER_OVERRIDES` JSON for per-printer hard overrides (by printer id or name)
- `WORKER_DISPATCH_TIMEOUT_MS` default timeout for Windows shared, socket, and IPP adapters
- SMB secrets can be referenced as `env:YOUR_VAR` or `WORKER_SECRET_<NORMALIZED_SECRET_REF>`
- Windows shared printer dispatch uses `smbclient`; the compose worker now installs it on startup

Example printer URIs:
- `mock://a4` (mock provider)
- `\\printserver\A4-FrontDesk` (Windows shared printer via `smbclient`)
- `smb://printserver/Zebra-Label` (Windows shared printer via `smbclient`)
- `socket://10.0.0.45:9100` (raw socket)
- `ipp://10.0.0.60/ipp/print` (IPP-over-HTTP adapter)

## Compose smoke validation

```bash
make smoke
# or: npm run smoke:compose
```

The smoke script:
1. boots isolated compose stack
2. seeds admin + config entities
3. runs worker once
4. verifies DB rows in `processed_files`, `print_jobs`, `print_job_pages`

## Tests

- Unit/integration-style app tests: `npm run test`
- Compose smoke E2E: `npm run test:e2e`
  - boots isolated stack
  - seeds admin config and routing/OCR settings
  - runs worker and validates dedup + DB persistence (`processed_files`, `print_jobs`, `print_job_pages`)

## Docs

- `docs/PLAN.md` — roadmap and delivery status
- `docs/ARCHITECTURE.md` — system design and boundaries
- `docs/AGENTS_SUB.md` — subagent scopes and statuses
- `EXTAUTH_API.md` — external auth integration contract

## Environment limitation

This repo includes production-shaped adapters for SMB and printer integrations.
Physical SMB auth/mounts and real printer dispatch are not validated in CI; local/CI runs use deterministic adapter behavior for repeatable tests.
