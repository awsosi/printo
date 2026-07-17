# ARCHITECTURE.md

Owner: `@draven`
Status: `ACTIVE`
Last updated: `2026-03-02`

Defines service boundaries, data flow, and docker-compose topology for `printo`.

## 1. System Topology (docker-compose)

Services:
- `web` — frontend/admin UI + API proxy (TypeScript)
- `api` — REST API + AAA + configuration (TypeScript)
- `worker` — scan/classify/route/dispatch pipeline (TypeScript)
- `vision` — OCR/barcode/rasterization page classifier (Python FastAPI, see `docs/VISION_SERVICE.md`)
- `db` — PostgreSQL
- `redis` — async backbone placeholder (ready for queue expansion)

All services expose `/health` and write structured logs to stdout.

Host ports are configurable via compose env vars:
`WEB_PORT`, `API_PORT`, `WORKER_PORT`, `VISION_PORT`, `DB_PORT`, `REDIS_PORT`.

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
- Page classification (heuristic text rules locally, Vision Service over HTTP, composite fallback)
- Routing decision engine (forced/visual rules → thermal patterns → classification routes → default)
- Dispatch provider abstraction (`mock`, `socket`, `ipp`, `windows`, `cups`) configurable per printer
- Persistent print job/page records with per-page classification diagnostics

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
6. Each page is classified as `OUTGOING_LABEL_THERMAL`, `RETURN_LABEL_A4`, or `DOCUMENT_A4`
   (Vision Service when configured, deterministic text heuristics otherwise).
7. Routing maps each page to `THERMAL` or `A4` with precedence:
   forced (image snippet / visual profile) → visual rectangle rules → thermal label
   patterns → classification routes (confidence-gated, per routing profile) → profile default.
8. Dispatcher resolves provider per printer (`mock`/`socket`/`ipp`/`windows`/`cups`) and submits
   print actions. The `cups` provider extracts the single page as a standalone PDF
   (A4: `-o media=A4 -o fit-to-page`, so return labels scale onto A4) and passes raw
   ZPL through to raw thermal queues (`-o raw`).
9. Worker stores processed file + job/page outcomes including page class, confidence, carrier.

## 7. OCR/Vision Abstraction

Contract:
- `analyze(file, provider, config) -> { pages[] }`

Implemented providers:
- `mock` (deterministic, CI-safe)

Page classification (separate from the OCR provider seam):
- `heuristic` — carrier signatures (DHL/UPS/FedEx/DPD/GLS/InPost/Poczta Polska), label/return/
  document keywords (EN/PL/DE), tracking-number patterns, label-sized-page detection.
- `vision-service` — HTTP client for the Python Vision Service (PaddleOCR + zxing-cpp barcodes +
  pypdfium2 rasterization as optional layers). Contract in `docs/VISION_SERVICE.md`.
- `composite` — vision first, heuristic fallback; selected via `WORKER_CLASSIFIER` /
  `WORKER_VISION_URL` / `WORKER_VISION_TIMEOUT_MS`.

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

- Structured service logs (`page_routed`, `page_dispatch_failed`, `page_classification_failed`,
  `vision_classify_fallback` events with job/page/class/confidence context)
- `/health` endpoints across services
- Worker `/metrics` (Prometheus text): jobs by status, classification distribution and
  confidence histogram, routing decisions by rule, per-page dispatch outcomes
- Pipeline summary endpoint (`/pipeline/status`) for worker state

## 11. CI/CD Baseline

GitHub Actions pipeline:
1. install (`npm ci`)
2. test
3. lint
4. typecheck
5. build
6. E2E smoke (`npm run test:e2e` -> compose smoke flow)

Compose smoke validation is available via `npm run smoke:compose` and `make smoke`.

## 12. External Validation Limits

In this environment, physical network SMB auth and real printer dispatch are not directly testable end-to-end.
Architecture keeps those concerns behind scanner/dispatcher adapters so production integrations can be attached without changing core pipeline logic; compose defaults to deterministic mock dispatch while preserving opt-in real `smbclient`/`socket`/`ipp` paths.
