export interface ExternalAuthResult {
  ok: boolean;
  authenticated: boolean;
  remoteUserId?: string;
  username?: string;
  reason?: string;
}

interface RawExternalAuthResponse {
  success: boolean;
  authenticated: boolean;
  error: string | null;
  user_id?: number;
  username?: string;
}

function toPositiveInteger(value: string | undefined, fallback: number): number {
  if (!value) {
    return fallback;
  }

  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed < 0) {
    return fallback;
  }

  return Math.trunc(parsed);
}

function mapAuthFailureReason(error: string | null): string {
  const normalized = (error ?? '').trim().toUpperCase();

  if (normalized === 'ACCOUNT_DISABLED') {
    return 'ACCOUNT_DISABLED';
  }

  if (normalized === 'ACCESS_DENIED') {
    return 'ACCESS_DENIED';
  }

  return 'INVALID_CREDENTIALS';
}

async function sleep(ms: number): Promise<void> {
  if (ms <= 0) {
    return;
  }

  await new Promise<void>((resolve) => {
    setTimeout(resolve, ms);
  });
}

export async function authenticateExternal(username: string, password: string): Promise<ExternalAuthResult> {
  const baseUrl = process.env.EXTAUTH_BASE_URL;
  const apiKey = process.env.EXTAUTH_API_KEY;

  if (!baseUrl || !apiKey) {
    return { ok: false, authenticated: false, reason: 'EXTAUTH_NOT_CONFIGURED' };
  }

  const timeoutMs = toPositiveInteger(process.env.EXTAUTH_TIMEOUT_MS, 3000);
  const maxRetries = toPositiveInteger(process.env.EXTAUTH_MAX_RETRIES, 2);
  const retryBaseMs = toPositiveInteger(process.env.EXTAUTH_RETRY_BASE_MS, 200);

  const url = new URL('/RFM_Auth', baseUrl);
  url.searchParams.set('ApiKey', apiKey);
  url.searchParams.set('UserName', username);
  url.searchParams.set('Password', password);

  for (let attempt = 0; attempt <= maxRetries; attempt += 1) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), timeoutMs);

    try {
      const response = await fetch(url, { method: 'GET', signal: controller.signal });
      if (!response.ok) {
        return { ok: false, authenticated: false, reason: 'REQUEST_REJECTED' };
      }

      const raw = (await response.json()) as RawExternalAuthResponse;

      if (!raw.success) {
        return { ok: false, authenticated: false, reason: 'REQUEST_REJECTED' };
      }

      if (!raw.authenticated) {
        return {
          ok: true,
          authenticated: false,
          reason: mapAuthFailureReason(raw.error)
        };
      }

      return {
        ok: true,
        authenticated: true,
        remoteUserId: raw.user_id?.toString(),
        username: raw.username ?? username
      };
    } catch {
      if (attempt >= maxRetries) {
        return { ok: false, authenticated: false, reason: 'UPSTREAM_UNREACHABLE' };
      }

      const backoffMs = retryBaseMs * 2 ** attempt;
      await sleep(backoffMs);
    } finally {
      clearTimeout(timeout);
    }
  }

  return { ok: false, authenticated: false, reason: 'UPSTREAM_UNREACHABLE' };
}
