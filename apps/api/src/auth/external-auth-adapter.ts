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

export async function authenticateExternal(username: string, password: string): Promise<ExternalAuthResult> {
  const baseUrl = process.env.EXTAUTH_BASE_URL;
  const apiKey = process.env.EXTAUTH_API_KEY;

  if (!baseUrl || !apiKey) {
    return { ok: false, authenticated: false, reason: 'EXTAUTH_NOT_CONFIGURED' };
  }

  const url = new URL('/RFM_Auth', baseUrl);
  url.searchParams.set('ApiKey', apiKey);
  url.searchParams.set('UserName', username);
  url.searchParams.set('Password', password);

  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 3000);

  try {
    const response = await fetch(url, { method: 'GET', signal: controller.signal });
    const raw = (await response.json()) as RawExternalAuthResponse;

    if (!raw.success) {
      return { ok: false, authenticated: false, reason: 'REQUEST_REJECTED' };
    }

    if (!raw.authenticated) {
      return { ok: true, authenticated: false, reason: 'INVALID_CREDENTIALS' };
    }

    return {
      ok: true,
      authenticated: true,
      remoteUserId: raw.user_id?.toString(),
      username: raw.username ?? username
    };
  } catch {
    return { ok: false, authenticated: false, reason: 'UPSTREAM_UNREACHABLE' };
  } finally {
    clearTimeout(timeout);
  }
}
