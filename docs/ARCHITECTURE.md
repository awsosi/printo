# ARCHITECTURE.md

Owner: `@draven`
Status: `ACTIVE`
Last updated: `2026-02-27`

Defines service boundaries, data flow, and docker-compose topology for `printo`.

## 1. System Topology (docker-compose)

Minimum services:
- `web` — frontend SPA/SSR (TypeScript)
- `api` — REST API + AAA + config orchestration (TypeScript)
- `worker` — SMB scan, OCR/vision, routing, print dispatch (TypeScript)
- `db` — PostgreSQL
- `redis` — queue/event backbone for async jobs

Optional later:
- `otel-collector` (or lightweight metrics endpoint aggregator)

All services expose:
- `/health` endpoint (liveness/readiness basics)
- structured logs to stdout

## 2. Bounded Contexts

### Identity & Access (API)
Responsibilities:
- Local account CRUD
- Local session/JWT lifecycle
- Role-based authorization (`USER`, `ADMIN`)
- Remote account verification through `EXTAUTH_API.md` adapter only
- Audit logging for sensitive actions

### Configuration (API)
Responsibilities:
- User SMB source config (path + credentials)
- Filename mask definitions
- Printer config (A4 + thermal)
- Page routing profiles
- Global OCR config + per-user overrides
- User language/theme preferences

### Processing Pipeline (Worker)
Responsibilities:
- Scheduled/continuous SMB polling
- Candidate file filtering by masks
- Dedup via `processed_files`
- OCR/visual classification through provider interface
- Route pages/documents to A4/thermal printer queues
- Persist job statuses + audit events

### Presentation (Web)
Responsibilities:
- Admin panels for all configuration entities
- User self-service settings
- Auth flows and guarded routes
- i18n + theme runtime preferences

## 3. Data Model (PostgreSQL, high level)

Core tables (minimum):
- `users`
- `user_credentials_local` (or equivalent local auth material)
- `user_roles`
- `sessions` / `refresh_tokens`
- `audit_log`
- `remote_auth_profiles` (endpoint/api key references, policy)
- `smb_sources`
- `filename_masks`
- `printers`
- `user_printer_assignments`
- `routing_profiles`
- `ocr_config_global`
- `ocr_config_user_override`
- `processed_files`
- `print_jobs`
- `print_job_pages` (or equivalent split mapping)

Indexes and constraints required for:
- dedup (`processed_files`: unique checksum/path+mtime strategy)
- fast queue/job status lookups
- role and ownership filtering

## 4. Authentication, Authorization, Auditing (AAA)

### Authentication
- Local auth: username/password with secure hash (Argon2id/bcrypt)
- Remote auth: adapter calls external endpoint from `EXTAUTH_API.md`
- Combined mode policy:
  - local-only for local users
  - remote-check for users flagged as remote-enabled
- Session strategy: short-lived access token + refresh token

### Authorization
- RBAC enforced in API middleware/guards
- Roles:
  - `ADMIN`: full CRUD and global config
  - `USER`: own settings and self data only
- UI route guards mirror API permissions (never trust frontend alone)

### Auditing
Audit entries for:
- auth attempts (local/remote)
- user CRUD
- config changes (SMB/printers/OCR/masks/routing)
- pipeline decisions and print dispatch outcomes

## 5. External Auth Contract

- Single integration point: `api` service adapter module
- No direct calls from `web` or `worker`
- Adapter behavior:
  - timeout + retry (bounded)
  - normalized result mapping
  - non-sensitive error propagation
  - audit event on each remote auth attempt

Spec source of truth: `EXTAUTH_API.md`.

## 6. Processing Pipeline Flow

1. Worker reads active SMB source configs.
2. Worker authenticates to SMB using configured domain account (`EXAMPLE\\serviceuser`) and secret.
3. Worker scans path and filters files by masks.
4. Worker computes dedup identity and skips processed files.
5. Worker sends document/page inputs to OCR/vision provider abstraction.
6. Routing engine applies rules (e.g., labels → thermal, rest → A4).
7. Worker dispatches print jobs to configured printer targets.
8. Worker records processed file + job/page outcomes + audit events.

## 7. OCR / Vision Provider Abstraction

Interface contracts:
- `analyzeDocument(input) -> structured layout/tags/pages`
- `classifyPage(page) -> labels/confidence`

Provider modes:
- `mock` (tests/dev)
- `tesseract` (local/offline baseline)
- `external` (future cloud providers)

Routing must consume normalized provider output, not vendor-specific payloads.

## 8. i18n & Theme

### i18n
- Locale files in JSON modules, namespaced by feature
- Resolution order:
  1. user preference
  2. browser preference
  3. default `en-US`
- Missing file or key must fallback to `en-US`
- API error/message keys should be translatable on frontend

### Theme
- Modes: `system`, `light`, `dark`
- Default: `system`
- Persist per-user override in DB

## 9. Security and Secret Handling

- No plaintext secrets in repo
- Env-driven secret injection (`.env`, compose overrides)
- Credentials at rest encrypted or restricted (minimum: strong hashing for passwords + sensitive config protection)
- Principle of least privilege in DB roles and service access

## 10. Observability Baseline

- Structured JSON logs with service name + request/job correlation id
- Health endpoints for all containers
- Minimal metrics (jobs processed, failures, queue depth) exposed for scraping/logging

## 11. CI/CD Baseline

Pipeline stages:
1. lint + format check
2. typecheck
3. unit/integration tests
4. build images/services
5. optional compose smoke test

Artifacts/logging retained for troubleshooting.

## 12. Repository Layout (target)

- `apps/web`
- `apps/api`
- `apps/worker`
- `packages/shared` (types/config/utils)
- `infra/docker-compose.yml`
- `infra/migrations`
- `docs/*`

(Exact paths may vary; boundaries must remain intact.)
