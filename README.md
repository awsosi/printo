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
- `WORKER_PRINTER_PROVIDER_OVERRIDES` (per-printer `provider`, `targetUri`, `timeoutMs`, `lpOptions`)
- `WORKER_SECRET_<NORMALIZED_SECRET_REF>`
- `WORKER_VISION_URL` (Vision Service base URL; unset = heuristic-only classification)
- `WORKER_CLASSIFIER` (`heuristic` | `vision` | `auto`)
- `WORKER_VISION_TIMEOUT_MS`

Provider behavior:

- `mock` is the default deterministic mode used for local smoke runs and CI
- scanner `auto` uses filesystem scanning for normal paths and `smbclient` handling for UNC-style SMB paths
- printer dispatch can resolve from `targetUri` values such as `cups://`, `smb://`, `socket://`, and `ipp://`
- `cups://` is the recommended target for new configs: A4 pages are submitted per page with
  `-o media=A4 -o fit-to-page` (return labels scale onto A4), thermal ZPL passes through raw
  (`lpadmin -p Zebra-Label -E -v ipp://<printer-ip>/ipp/print -m raw`)

Example printer URIs:

- `mock://a4`
- `cups://OfficeA4`
- `cups://cups.local:631/Zebra-Label`
- `\\\\printserver\\A4-FrontDesk`
- `smb://printserver/Zebra-Label`
- `socket://10.0.0.45:9100`
- `ipp://10.0.0.60/ipp/print`

## Page classification

Every page is classified as `OUTGOING_LABEL_THERMAL`, `RETURN_LABEL_A4`, or `DOCUMENT_A4`:

- Worker-local heuristics (deterministic, CI-safe): carrier signatures for DHL/UPS/FedEx/DPD/GLS/
  InPost/Poczta Polska, label/return/document keywords (EN/PL/DE), tracking-number patterns,
  label-sized-page detection.
- Vision Service (`services/vision`, Python FastAPI): rasterization (pypdfium2), barcode detection
  (zxing-cpp — Code 128/GS1-128, MaxiCode, DataMatrix), OCR (PaddleOCR) for scanned pages.
  Contract: `docs/VISION_SERVICE.md`. The worker falls back to heuristics when unreachable.

Routing profiles carry `classificationRoutes` (page class → route type + optional printer +
minimum confidence); without configuration, outgoing labels route to `THERMAL` and return labels
stay on `A4`. Explicit visual rules and thermal label patterns always take precedence over the
classifier. The admin UI (Routing profiles tab) provides a full editor for these rules, a live
"Preview routing" that runs an uploaded PDF through the same classifier the worker uses
(`POST /pipeline/preview/classification`), per-page classification diagnostics on job cards in
the Status tab, a classification-engine health card (`GET /pipeline/vision-status`), and a
"Run pipeline now" control.

Demo fixture: `node scripts/generate-mixed-fixture.mjs` regenerates
`fixtures/intake/mixed-carriers.pdf` (invoice + DHL label + packing slip + UPS return label +
FedEx label); `apps/worker/tests/e2e-mixed-routing.test.ts` runs it through the full pipeline.

Worker metrics: `GET :5000/metrics` (Prometheus text) — job outcomes, page-class distribution,
classification confidence histogram, routing decisions, dispatch success/failure.

## Routing engine and Windows agent

The routing rules live in one place and are executed by two implementations, so a workstation
and the server route a page identically.

- `packages/routing-engine` — TypeScript: rule schema, feature model, evaluation engine,
  placement maths. Built-in profiles are exported to `profiles/*.json`.
- `clients/windows/` — the Windows agent (.NET 10):
  - `Printo.Agent.Core` — the C# port of the engine; embeds `profiles/*.json`
  - `Printo.Agent.Render` — PDFium rendering, region crop, rasters, PNG, feature extraction,
    zxing-cpp barcodes
  - `Printo.Agent.Printing` — GDI output, raw ZPL, printer profiles and discovery
  - `Printo.Agent.Ocr` — the inbox Windows recogniser
  - `Printo.Agent.Tests` — unit, conformance, render-diff and corpus-parity tests
- `tests/conformance/` — shared fixtures both engines execute; a divergence fails the build
- `tests/corpus/` — extracted features and reviewed ground truth for the 1266-page sample set
- `tests/render/` — reference images for the render-diff suite

```bash
# TypeScript engine, including the golden corpus
npx vitest run --root packages/routing-engine

# Windows agent
dotnet test clients/windows/Printo.Agent.Tests

# after editing packages/routing-engine/src/profiles.ts
npx tsx packages/routing-engine/scripts/export-profiles.ts

# accept new render-diff output, after reviewing the images
PRINTO_UPDATE_REFERENCES=1 dotnet test clients/windows/Printo.Agent.Tests
```

The sample PDFs are customer data and live outside the repository; corpus tests skip when they
are absent. Point `PRINTO_CORPUS_DIR` at them or keep them beside the checkout.

Regenerate the extracted corpus (needs `pypdfium2`, `numpy`, `zxing-cpp`, `rapidocr-onnxruntime`):

```bash
python tools/corpus/extract_features.py <corpus-dir> --out tests/corpus/features.jsonl.gz
python tools/corpus/label_corpus.py tests/corpus/features.jsonl.gz --out tests/corpus/expected.json
```

Printing on physical A4 and thermal hardware has not been verified yet; that pass is scheduled
with the customer and no test claims it.

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
