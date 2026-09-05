# PLAN.md

Owner: `@draven`
Status: `ACTIVE`
Last updated: `2026-03-02`

Single source of truth for delivery state of `printo`.

## Mission
Deliver a docker-compose, TypeScript + PostgreSQL E2E application for PDF intake, OCR/visual routing, and split printing (A4 + thermal) with AAA, i18n fallback, theme preferences, CI, and tests.

## Phase Status

### Phase 0 — Foundation & Contracts
- [x] Architecture boundaries defined (`docs/ARCHITECTURE.md`)
- [x] External auth contract finalized (`EXTAUTH_API.md`)
- [x] Monorepo service layout in place
- [x] Compose topology: `web`, `api`, `worker`, `db`, `redis`
- [x] `.env.example` baseline

### Phase 1 — Data Model + AAA Core
- [x] PostgreSQL schema + migrations for auth/config/pipeline entities
- [x] AuthN/AuthZ with RBAC (`USER`, `ADMIN`)
- [x] Audit events for login + config changes
- [x] Remote auth path wired strictly via EXTAUTH adapter

### Phase 2 — Config API + Admin UI
- [x] Local user CRUD for admin
- [x] SMB source config CRUD
- [x] Filename mask CRUD
- [x] Printer config CRUD + per-user assignment CRUD
- [x] Routing profile CRUD
- [x] OCR global + per-user override CRUD
- [x] User locale/theme preference API + UI
- [x] i18n JSON fallback to `en-US`

### Phase 3 — Intake, OCR/Visual, Routing, Print
- [x] Worker polling runner + run-once endpoint
- [x] SMB scanner adapter path: `auto` mode + `smbclient` UNC scanner + filesystem fallback
- [x] Filename mask filtering
- [x] Dedup via `processed_files`
- [x] OCR/visual abstraction with mock provider
- [x] Routing labels → thermal, fallback → A4
- [x] Printer dispatch provider abstraction hardened (`mock` / `socket` / `ipp`) with per-printer overrides
- [x] Job/page persistence

### Phase 4 — Test Matrix + E2E
- [x] Unit tests for auth/RBAC + worker pipeline + i18n behavior
- [x] E2E compose smoke happy path (admin config -> worker run -> DB verification)
- [x] Deterministic OCR/dispatch mocks for tests

### Phase 5 — DevEx, CI/CD, Delivery Docs
- [x] `Makefile` tasks for dev/test/build/lint/migrate/compose
- [x] CI workflow (lint + tests + typecheck + build + E2E smoke)
- [x] Compose smoke script hardened (isolated project name, custom high ports, retry-safe API calls)
- [x] README + architecture + subagent docs synchronized

### Phase 6 — Windows agent + shared routing engine (in progress)

Tracked in `docs/WINDOWS_CLIENT_PLAN.md`; summarised here so this file stays the single view
of delivery state.

- [x] Shared routing rule schema, two implementations (`packages/routing-engine` in TypeScript,
      `clients/windows/Printo.Agent.Core` in C#), held together by shared conformance fixtures
      in `tests/conformance/` that both engines execute
- [x] Corpus feature extraction and golden corpus: 1266 pages routed correctly in both
      text-layer modes (`tools/corpus/`, `tests/corpus/`)
- [x] Carrier resolution reworked; the `*GLS certified label*` false positive is fixed in the
      worker heuristic and cannot recur in the new engine
- [x] Agent-side feature extraction (PDFium geometry, ink box, text, zxing-cpp barcodes, inbox
      Windows OCR) with a parity test against the calibrated extractor
- [x] Print output: region crop, transform maths, whole-sheet composition against the printable
      area, GDI device, raw ZPL, printer profiles, discovery, render-diff suite
- [ ] Capture spike (M1) — blocked on one elevated `Add-Printer`
- [ ] Agent runtime: service, tray, spool, hot folders, fallback picker (M4)
- [ ] Server integration: agent APIs, bundle sync, decision modes, review queue (M5)
- [ ] Admin UI: rule editor, agents, fallback analytics (M6)
- [ ] Packaging, signing, GPO deployment (M7); hardening (M8)

Hardware verification on real A4 and thermal printers is postponed to a joint session with
the customer and is **not** claimed by any test.

## Definition of Done
- [x] Docker-compose stack runs locally
- [x] Admin UI can configure users, SMB sources, masks, printers, user printer assignments, OCR, routing
- [x] User settings endpoint for own locale/theme
- [x] Worker scans configured source paths and routes print pages
- [x] Processed-file dedup persisted in DB
- [x] Migrations and schema committed
- [x] Automated unit + E2E coverage present
- [x] Required docs up to date

## Work Split (agreed)
- `@draven`: architecture, acceptance gates, docs truth, delivery verification
- `@virex`: implementation, tests, compose, CI, smoke reliability

## Hard External Limits (current)
- Real network SMB authentication/mount and physical printer dispatch cannot be fully validated in this environment; stack provides a production-shaped adapter path with deterministic local simulation for CI/dev.
