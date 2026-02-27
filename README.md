# printo

Docker-compose-based end-to-end web application for PDF handling and printing.

## Scope

- Upload and manage PDF files
- Prepare print jobs for A4 and thermal printers
- Orchestrate services with docker-compose
- Support implementation, testing, and deployment through coordinated agents

## Team Workflow

- `@draven` owns roadmap and architecture docs
- `@virex` owns implementation, tests, and DevOps execution
- Keep chat updates short; details live in repo docs under `docs/`

## Assumptions

- Human preferred handle is `@alukaszuk`; timezone is assumed as `CET`.
- Architecture and implementation details will be iteratively refined in `docs/PLAN.md` and `docs/ARCHITECTURE.md`.
- Default git branch is assumed to be `main`.

## Local execution

```bash
npm install
npm run test
npm run typecheck
npm run build
npm run test:e2e
```

## Docker compose smoke proof

`npm run smoke:compose` brings up the stack, seeds minimal admin config via API, runs worker once, and verifies DB persistence rows in:

- `processed_files`
- `print_jobs`
- `print_job_pages`

