# EXTAUTH_API.md

Owner: `@draven`
Status: `ACTIVE`
Last updated: `2026-02-27`

Source of truth for remote authentication integration used by `apps/api` only.

## 1) Integration Rule

- Only `apps/api` may call this endpoint.
- `apps/web` and `apps/worker` must never call EXTAUTH directly.
- Every attempt must generate an `audit_log` entry (`AUTH_REMOTE_ATTEMPT`).

## 2) Endpoint Contract

**Base URL (env):** `EXTAUTH_BASE_URL`

**Method:** `GET`

**Path:** `/RFM_Auth`

### Query params

| Name | Required | Example |
|---|---|---|
| `ApiKey` | yes | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` |
| `UserName` | yes | `john_doe` |
| `Password` | yes | `secret` |

### Example

```http
GET /RFM_Auth?ApiKey=uuid-key&UserName=john_doe&Password=secret HTTP/1.1
Host: ff.vitkac.local
```

## 3) Raw Response (provider)

```json
{
  "success": true,
  "authenticated": true,
  "error": null,
  "user_id": 12345,
  "username": "john_doe"
}
```

## 4) API Adapter Normalization

`apps/api` converts provider response into internal shape:

```ts
interface ExternalAuthResult {
  ok: boolean;                 // transport + provider processed
  authenticated: boolean;      // credential result
  remoteUserId?: string;
  username?: string;
  reason?: string;             // sanitized
}
```

### Mapping rules

- `success=false` -> `{ ok:false, authenticated:false, reason:"REQUEST_REJECTED" }`
- `success=true, authenticated=false` -> `{ ok:true, authenticated:false, reason:"INVALID_CREDENTIALS" | "ACCOUNT_DISABLED" | "ACCESS_DENIED" }`
- `success=true, authenticated=true` -> `{ ok:true, authenticated:true, remoteUserId, username }`
- timeout/network -> `{ ok:false, authenticated:false, reason:"UPSTREAM_UNREACHABLE" }`

## 5) Reliability Policy

- Timeout: `3000ms` default
- Retries: max 2 with short backoff (200ms, 400ms)
- No raw upstream error leakage to frontend; return generic auth failure message.

## 6) Security Rules

- API key loaded from `EXTAUTH_API_KEY` env var.
- Never log password or api key.
- Do not persist remote plaintext credentials.

## 7) Audit Requirements

Write audit event on each call:

- `action`: `AUTH_REMOTE_ATTEMPT`
- `status`: `SUCCESS` / `FAILURE`
- `metadata`: `{ username, ok, authenticated, reason? }`

## 8) Compatibility Notes

Current provider returns HTTP `200` for both success and auth failures. Adapter must inspect JSON body fields and not rely on HTTP code for auth decision.

## 9) Implementation Status (live)

Implemented in `apps/api`:

- `/auth/login` routes `is_remote_enabled` users through `authenticateExternal(...)`.
- Remote auth attempts are audited as `AUTH_REMOTE_ATTEMPT` with `{ ok, authenticated, reason }` metadata.
- Failures map to normalized reasons:
  - `INVALID_CREDENTIALS`
  - `ACCOUNT_DISABLED`
  - `ACCESS_DENIED`
  - `UPSTREAM_UNREACHABLE`
- Retry/backoff and timeout are configurable via env:
  - `EXTAUTH_TIMEOUT_MS` (default `3000`)
  - `EXTAUTH_MAX_RETRIES` (default `2`)
  - `EXTAUTH_RETRY_BASE_MS` (default `200`)
