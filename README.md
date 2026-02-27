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

## Run with docker-compose

```bash
make up
# web:    http://127.0.0.1:${WEB_PORT:-3000}
# api:    http://127.0.0.1:${API_PORT:-4000}/health
# worker: http://127.0.0.1:${WORKER_PORT:-5000}/health

make down
```

Host ports are configurable in `.env.example`:
- `WEB_PORT`, `API_PORT`, `WORKER_PORT`, `DB_PORT`, `REDIS_PORT`

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
- Playwright E2E: `npm run test:e2e`
  - health endpoints
  - remote auth flow
  - happy path admin config → worker processing → routed pages (`THERMAL` + `A4`) with dedup check

## Docs

- `docs/PLAN.md` — roadmap and delivery status
- `docs/ARCHITECTURE.md` — system design and boundaries
- `docs/AGENTS_SUB.md` — subagent scopes and statuses
- `EXTAUTH_API.md` — external auth integration contract

## Environment limitation

This repo includes production-shaped adapters for SMB and printer integrations.
Physical SMB auth/mounts and real printer dispatch are not validated in CI; local/CI runs use deterministic adapter behavior for repeatable tests.
