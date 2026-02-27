# PLAN.md

Owner: `@draven`
Status: `ACTIVE`
Last updated: `2026-02-27`

Single source of truth for roadmap, milestones, and delivery sequencing for `printo`.

## Mission
Deliver a production-shaped, docker-compose-based E2E web application for PDF intake, OCR/visual routing, and split printing (A4 + thermal), with full AAA, PostgreSQL persistence, and minimal CI/CD.

## Constraints (hard)
- Project root: `/home/openclaw/projects/printo`
- Stack: TypeScript services + PostgreSQL + modern web frontend
- i18n JSON modules with fallback to `en-US` on missing file/key
- Theme defaults to browser/system with per-user override
- Remote auth checks must go only through `EXTAUTH_API.md`

## Autonomous Session Protocol (enforced)
- Coordination only in Rocket.Chat room `printo` with explicit `@mentions`.
- Update cadence: milestone start, milestone done (with commit + commands run), blocker; otherwise stay heads-down.
- `@draven` owns architecture, sequencing, acceptance gates, and docs truth in this file + `docs/ARCHITECTURE.md`.
- `@virex` owns implementation, tests, docker-compose, CI/CD execution, and E2E flow completion.
- Do not run maintenance-only host commands (e.g., `openclaw doctor --repair`) unless re-approved by `@alukaszuk`.

## Active Autonomous Queue (current)
1. `@virex`: Admin UI completion for SMB/Printers/Masks/Routing/OCR + i18n fallback + theme override persistence + tests.
2. `@virex`: Worker intake loop (SMB poll + masks + dedup + OCR abstraction + routing + print dispatch adapters).
3. `@virex`: Full E2E happy path (admin config -> worker processing -> routed print outcome) with deterministic fixtures/mocks.
4. `@virex`: docker-compose reproducibility + README runbook + remaining Phase 4/5 checklist closure.
5. `@draven`: acceptance review and gatekeeping per phase exits + docs synchronization.

## Delivery Phases

### Phase 0 — Foundation & Contracts
**Owner:** `@draven` (design), `@virex` (repo bootstrap)

- [ ] Finalize architecture boundaries in `docs/ARCHITECTURE.md`
- [ ] Finalize external auth contract in `EXTAUTH_API.md`
- [ ] Create monorepo/service structure and shared TS config
- [ ] Create docker-compose baseline (web, api, worker, postgres, redis)
- [ ] Add `.env.example` and secrets handling pattern

**Exit criteria:** repo boots with placeholder services and health endpoints.

---

### Phase 1 — Data Model + AAA Core
**Owner:** `@virex`

- [ ] PostgreSQL schema + migrations for:
  - local users
  - roles (`USER`, `ADMIN`)
  - sessions/tokens
  - audit log
  - SMB source configs
  - filename masks
  - printer configs
  - routing profiles
  - OCR config (global + per-user overrides)
  - processed file dedup log
- [ ] AuthN/AuthZ middleware + RBAC guards
- [ ] Audit events for login/config changes/print actions

**Exit criteria:** API enforces role checks and audit trail for protected actions.

---

### Phase 2 — Config API + Admin UI
**Owner:** `@virex`

- [x] Admin CRUD for local users (API baseline: list/create/role update/delete + tests)
- [ ] Admin config screens/endpoints for:
  - [x] SMB path + domain credentials (API CRUD + RBAC tests + admin UI wiring)
  - [x] filename masks (API CRUD + RBAC tests)
  - [x] A4/thermal printer setup (API CRUD + RBAC tests + admin UI wiring)
  - [x] page routing rules (A4 vs thermal) (API CRUD + RBAC tests)
  - [x] global OCR config + user overrides (API CRUD + RBAC tests)
- [x] USER scope: self-view/edit allowed subset (API baseline: `/me/preferences` read/write)
- [ ] i18n loader + fallback strategy in UI and API responses
- [ ] Theme system preference + per-user override persisted

**Exit criteria:** admin can configure all required entities end-to-end from UI.

---

### Phase 3 — Intake, OCR/Visual, Routing, Print
**Owner:** `@virex`

- [ ] Worker polls SMB paths using configured service credentials
- [ ] Filename mask filtering
- [ ] Dedup check and processed-file persistence
- [ ] OCR/vision provider abstraction (vendor-neutral adapter)
- [ ] Routing engine: labels → thermal, remainder → A4 (configurable)
- [ ] Print dispatch adapters (A4/thermal network targets)
- [ ] Job status + retries + audit logging

**Exit criteria:** new file in SMB path flows to correct printer(s) with persisted history.

---

### Phase 4 — Test Matrix + E2E
**Owner:** `@virex` (+ subagents)

- [ ] Unit tests for auth, RBAC, routing, dedup, i18n fallback
- [ ] Integration tests for API + DB
- [ ] At least one E2E happy path (admin config → worker scan → routed print job)
- [ ] Test fixtures/mocks for OCR provider + printer dispatch

**Exit criteria:** CI runs tests non-interactively and passes on clean checkout.

---

### Phase 5 — DevEx, CI/CD, Delivery Docs
**Owner:** `@virex`, reviewed by `@draven`

- [ ] `Makefile` tasks: `dev`, `test`, `build`, `up`, `down`, `lint`, `migrate`
- [x] CI workflow baseline: tests + typecheck + build + Playwright E2E gate
- [ ] Extend CI with lint gate (after lint script/tooling is added)
- [ ] Readme runbook and architecture/docs sync
- [ ] Final hardening pass on logs/healthchecks/observability baseline

**Exit criteria:** `docker compose up` + E2E smoke flow documented and reproducible.

## Definition of Done Checklist
- [ ] Full docker-compose stack starts locally
- [ ] Admin UI configures users/printers/OCR/masks
- [ ] USER UI exposes own settings
- [ ] Worker scans SMB, performs OCR/visual checks, routes jobs
- [ ] Processed files are deduplicated by DB log
- [ ] Schema + migrations committed
- [ ] Automated tests include unit + at least one E2E happy path
- [ ] `README.md`, `docs/PLAN.md`, `docs/ARCHITECTURE.md`, `docs/AGENTS_SUB.md`, `EXTAUTH_API.md` are up to date

## Active Work Split
- `@draven`: architecture, sequencing, acceptance gates, review
- `@virex`: implementation, tests, docker-compose, CI/CD, E2E execution

## Risks / External Limits
- SMB and printer access may require local network resources unavailable in CI; provide mocked adapters and a local simulation mode.
- OCR vendor keys may be unavailable; implement pluggable providers with at least one offline/mock provider for tests.
- Remote auth endpoint may be unreachable during dev; provide timeout/retry and deterministic mock mode behind feature flag.
