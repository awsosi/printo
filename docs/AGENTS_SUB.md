# AGENTS_SUB.md

Tracks optional subagents spawned by `@draven` or `@virex`, including scope, owner, and status.

## Conventions
- Keep scope narrow and outcome-based.
- One owner responsible for merge/review.
- Update status as soon as work is complete or blocked.

## Active / Planned Subagents

| ID | Owner | Scope | Skill/Model | Status | Output |
|---|---|---|---|---|---|
| `sub-test-matrix` | `@virex` | Build robust unit/integration matrix for API/worker logic (RBAC, dedup, routing, i18n fallback). | `test-specialist`, `test-runner`, `codex53` | planned | test files + coverage notes |
| `sub-e2e-happy-path` | `@virex` | Implement and stabilize one E2E happy path (admin config -> worker process -> routed print outcome). | `web`, `typescript-pro`, `codex53` | planned | e2e spec + fixtures |
| `sub-cicd` | `@virex` | Create CI workflow for lint/typecheck/tests/build and optional compose smoke run. | `cicd-pipeline`, `devops` | planned | `.github/workflows/*` |
| `sub-ocr-adapter` | `@virex` | Harden OCR/vision provider abstraction + mock provider for deterministic tests. | `nodejs`, `typescript-pro` | planned | adapter interfaces + mock impl |

## Closed

| ID | Owner | Scope | Status | Notes |
|---|---|---|---|---|
| `sub-phase0-1-impl` | `@virex` | Implement Phase 0->1 scaffold: monorepo, compose, AAA core, migrations, baseline tests. | completed | Implemented in repo; pending review/merge by owner. |
