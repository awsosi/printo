# ARCHITECTURE.md

Owner: `@draven`
Status: `ACTIVE`
Last updated: `2026-02-27`

Defines service boundaries, data flow, and docker-compose topology for `printo`.

## 1. System Topology (docker-compose)

Services:
- `web` — frontend/admin UI + API proxy (TypeScript)
- `api` — REST API + AAA + configuration (TypeScript)
- `worker` — scan/ocr/route/dispatch pipeline (TypeScript)
- `db` — PostgreSQL
- `redis` — async backbone placeholder (ready for queue expansion)

All services expose `/health` and write structured logs to stdout.

Host ports are configurable via compose env vars:
`WEB_PORT`, `API_PORT`, `WORKER_PORT`, `DB_PORT`, `REDIS_PORT`.

## 2. Bounded Contexts

### Identity & Access (`api`)
- Local account lifecycle
- JWT access/refresh flow
- RBAC (`USER`, `ADMIN`)
- Remote login verification through EXTAUTH adapter only
- Audit logging for auth and admin actions

### Configuration (`api`)
- SMB source config per user/global
- Filename masks per user/global
- Printer config + per-user printer assignment
- Routing profile config
- OCR global + per-user override config
- User preference config (`locale`, `theme`)

### Processing (`worker`)
- Continuous interval runner + manual run endpoint
- Source scan adapter (`auto` mode with filesystem + `smbclient` UNC path support)
- Mask filtering + dedup guard
- OCR provider abstraction (`mock` baseline)
- Routing decision engine (thermal labels vs A4 fallback)
- Dispatch provider abstraction (`mock`, `socket`, `ipp`) configurable per printer
- Persistent print job/page records

### Presentation (`web`)
- Admin configuration surface for all required entities
- User preference controls
- i18n runtime loading with fallback
- Theme resolution (`system`/`light`/`dark`)

## 3. Data Model (PostgreSQL)

Core entities:
- `users`, `user_credentials_local`, `user_roles`, `refresh_tokens`
- `audit_log`
- `remote_auth_profiles`
- `smb_sources`, `filename_masks`
- `printers`, `user_printer_assignments`
- `routing_profiles`
- `ocr_config_global`, `ocr_config_user_override`
- `processed_files`, `print_jobs`, `print_job_pages`

Dedup guarantees:
- `processed_files.checksum_sha256` unique
- `processed_files(file_path, file_mtime)` unique

## 4. AAA Rules

### Authentication
- Local auth: hashed password verification
- Remote auth: EXTAUTH adapter with bounded timeout/retry
- User mode switch: `is_remote_enabled` routes login through EXTAUTH

### Authorization
- API middleware enforces JWT + role checks
- `ADMIN` only routes for management/config
- `USER` restricted to own scope endpoints

### Auditing
- Auth attempts (local + remote)
- User/config mutations
- Pipeline status can be joined through persisted print job records

## 5. External Auth Contract

`apps/api` is the only caller of EXTAUTH (`/RFM_Auth`) as defined in `EXTAUTH_API.md`.
No direct `web`/`worker` calls are allowed.

## 6. Processing Flow

1. Worker loads active source + mask + routing + OCR configs.
2. Scanner returns candidate PDFs from configured source paths.
3. Mask engine filters candidates.
4. Dedup check prevents re-processing.
5. OCR adapter normalizes page labels/text.
6. Routing maps each page to `THERMAL` or `A4`.
7. Dispatcher resolves provider per printer (`mock`/`socket`/`ipp`) and submits print actions.
8. Worker stores processed file + job/page outcomes.

## 7. OCR/Vision Abstraction

Contract:
- `analyze(file, provider, config) -> { pages[] }`

Implemented providers:
- `mock` (deterministic, CI-safe)

Planned provider slots:
- `tesseract` (offline)
- external cloud providers via adapter module

## 8. i18n and Theme

### i18n
Resolution order:
1. user preference
2. browser locale
3. default `en-US`

Missing locale file or key falls back to `en-US`.

### Theme
Modes: `system`, `light`, `dark`.
Stored per user and applied by web runtime.

## 9. Security/Secrets

- Secrets injected by env vars only
- No sensitive values committed to repo
- No plaintext remote API key/password logging

## 10. Observability Baseline

- Structured service logs
- `/health` endpoints across services
- Pipeline summary endpoint (`/pipeline/status`) for worker state

## 11. CI/CD Baseline

GitHub Actions pipeline:
1. install (`npm ci`)
2. test
3. lint
4. typecheck
5. build
6. Playwright E2E

Compose smoke validation is available via `npm run smoke:compose` and `make smoke`.

## 12. External Validation Limits

In this environment, physical network SMB auth and real printer dispatch are not directly testable end-to-end.
Architecture keeps those concerns behind scanner/dispatcher adapters so production integrations can be attached without changing core pipeline logic; compose defaults to deterministic mock dispatch while preserving opt-in real `smbclient`/`socket`/`ipp` paths.
