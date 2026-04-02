# printo

`printo` is a TypeScript monorepo for PDF intake, OCR/visual routing, and print dispatch. It runs as a small multi-service stack with an admin web shell, REST API, worker, PostgreSQL, and Redis-backed compose topology.

## Services

- `apps/web` — Express-based admin shell and API/worker proxy
- `apps/api` — REST API for auth, admin configuration, preferences, and audit data
- `apps/worker` — background pipeline and worker control endpoints
- `packages/shared` — shared PDF/image matching utilities
- `infra/docker-compose.yml` — local stack for `web`, `api`, `worker`, `db`, and `redis`

## Current feature set

- Local auth with bootstrap admin flow, login, refresh tokens, and RBAC (`USER`, `ADMIN`)
- Remote auth adapter via `EXTAUTH_API.md`
- Admin APIs for users, groups, group memberships, AD sync config/import, SMB sources, filename masks, printers, user printer assignments, routing profiles, visual profiles, OCR settings, system settings, and audit logs
- Worker endpoints for health, run-once execution, status, notifications, job history, page history, retry, and cancel
- Web UI for bootstrap/login, printer management, routing profile management, routing preview, notification testing, and pipeline job/status views
- i18n message loading with `en-US` fallback and `pl-PL` messages
- Theme preference storage via the API

## Repo commands

From the repo root:

```bash
npm ci
make lint
make test
make typecheck
make build
make smoke
```

Available shortcuts:

- `make dev`
- `make test`
- `make typecheck`
- `make lint`
- `make build`
- `make e2e`
- `make smoke`
- `make up`
- `make down`
- `make migrate`

## Local install note

`packages/shared` depends on `canvas`. On machines where a prebuilt binary is not available, `npm ci` falls back to a native build and requires the usual Cairo/Pango/Pixman toolchain.

CI uses Node 22 because `canvas@2.11.2` does not currently provide a Node 24 prebuilt binary in this repo's dependency set.

## Run with Docker Compose

```bash
docker compose -f infra/docker-compose.yml up -d
```

Default endpoints:

- web: `http://127.0.0.1:3000`
- api: `http://127.0.0.1:4000/health`
- worker: `http://127.0.0.1:5000/health`

Stop and clean up:

```bash
docker compose -f infra/docker-compose.yml down -v --remove-orphans
```

Or use:

```bash
make up
make down
```

Configurable ports are defined in `.env.example`:

- `WEB_PORT`
- `API_PORT`
- `WORKER_PORT`
- `DB_PORT`
- `REDIS_PORT`

## First-time setup

Open the web app and use the bootstrap admin form.

- `GET /auth/bootstrap-status` reports whether bootstrap is still allowed
- `POST /auth/bootstrap-admin` creates the initial admin once
- `BOOTSTRAP_ADMIN_TOKEN` can be set to require a one-time bootstrap token

## Worker configuration

Relevant environment variables from `.env.example`:

- `WORKER_POLL_INTERVAL_MS`
- `WORKER_SCANNER=auto`
- `WORKER_DISPATCH_PROVIDER_MODE=mock`
- `WORKER_DISPATCH_TIMEOUT_MS`
- `WORKER_PRINTER_PROVIDER_OVERRIDES`
- `WORKER_SECRET_<NORMALIZED_SECRET_REF>`

Provider behavior:

- `mock` is the default deterministic mode used for local smoke runs and CI
- scanner `auto` uses filesystem scanning for normal paths and `smbclient` handling for UNC-style SMB paths
- printer dispatch can resolve from `targetUri` values such as `smb://`, `socket://`, and `ipp://`

Example printer URIs:

- `mock://a4`
- `\\\\printserver\\A4-FrontDesk`
- `smb://printserver/Zebra-Label`
- `socket://10.0.0.45:9100`
- `ipp://10.0.0.60/ipp/print`

## Smoke test

```bash
make smoke
```

The compose smoke script boots an isolated stack, seeds the minimum config, runs the worker once, and verifies persisted rows in:

- `processed_files`
- `print_jobs`
- `print_job_pages`

## Tests

- `npm run test` runs workspace tests
- `npm run test:e2e` runs the compose smoke flow

## Docs

- `docs/PLAN.md` — delivery status
- `docs/ARCHITECTURE.md` — service boundaries and topology
- `docs/AGENTS_SUB.md` — implementation split notes
- `EXTAUTH_API.md` — external auth contract

## Limits

The repo includes production-shaped SMB and printer adapters, but CI and local smoke coverage stay on deterministic paths. Real SMB infrastructure and physical printer dispatch are not validated end to end in CI.
