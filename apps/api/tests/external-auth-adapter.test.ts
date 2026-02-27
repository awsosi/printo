import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { authenticateExternal } from '../src/auth/external-auth-adapter.js';

describe('external auth adapter', () => {
  const originalEnv = { ...process.env };

  beforeEach(() => {
    process.env = { ...originalEnv };
    delete process.env.EXTAUTH_BASE_URL;
    delete process.env.EXTAUTH_API_KEY;
    delete process.env.EXTAUTH_TIMEOUT_MS;
    delete process.env.EXTAUTH_MAX_RETRIES;
    delete process.env.EXTAUTH_RETRY_BASE_MS;
    vi.restoreAllMocks();
  });

  afterEach(() => {
    process.env = { ...originalEnv };
    vi.restoreAllMocks();
  });

  it('returns not configured when extauth env is missing', async () => {
    const result = await authenticateExternal('user', 'pass');

    expect(result).toEqual({
      ok: false,
      authenticated: false,
      reason: 'EXTAUTH_NOT_CONFIGURED'
    });
  });

  it('retries transient network errors and succeeds', async () => {
    process.env.EXTAUTH_BASE_URL = 'https://extauth.example';
    process.env.EXTAUTH_API_KEY = 'test-api-key';
    process.env.EXTAUTH_RETRY_BASE_MS = '0';
    process.env.EXTAUTH_TIMEOUT_MS = '50';

    const fetchMock = vi
      .fn<typeof fetch>()
      .mockRejectedValueOnce(new Error('network down'))
      .mockRejectedValueOnce(new Error('gateway timeout'))
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({ success: true, authenticated: true, user_id: 123, username: 'john_doe', error: null }),
          {
            status: 200,
            headers: {
              'content-type': 'application/json'
            }
          }
        )
      );

    vi.stubGlobal('fetch', fetchMock);

    const result = await authenticateExternal('john_doe', 'secret');

    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(result).toEqual({
      ok: true,
      authenticated: true,
      remoteUserId: '123',
      username: 'john_doe'
    });
  });

  it('maps provider auth failures to normalized reasons', async () => {
    process.env.EXTAUTH_BASE_URL = 'https://extauth.example';
    process.env.EXTAUTH_API_KEY = 'test-api-key';

    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({ success: true, authenticated: false, error: 'account_disabled' }), {
        status: 200,
        headers: {
          'content-type': 'application/json'
        }
      })
    );

    vi.stubGlobal('fetch', fetchMock);

    const result = await authenticateExternal('john_doe', 'secret');

    expect(result).toEqual({
      ok: true,
      authenticated: false,
      reason: 'ACCOUNT_DISABLED'
    });
  });

  it('returns request rejected when provider rejects request', async () => {
    process.env.EXTAUTH_BASE_URL = 'https://extauth.example';
    process.env.EXTAUTH_API_KEY = 'test-api-key';

    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({ success: false, authenticated: false, error: 'bad key' }), {
        status: 200,
        headers: {
          'content-type': 'application/json'
        }
      })
    );

    vi.stubGlobal('fetch', fetchMock);

    const result = await authenticateExternal('john_doe', 'secret');

    expect(result).toEqual({
      ok: false,
      authenticated: false,
      reason: 'REQUEST_REJECTED'
    });
  });

  it('returns upstream unreachable after retry exhaustion', async () => {
    process.env.EXTAUTH_BASE_URL = 'https://extauth.example';
    process.env.EXTAUTH_API_KEY = 'test-api-key';
    process.env.EXTAUTH_MAX_RETRIES = '2';
    process.env.EXTAUTH_RETRY_BASE_MS = '0';

    const fetchMock = vi.fn<typeof fetch>().mockRejectedValue(new Error('network down'));
    vi.stubGlobal('fetch', fetchMock);

    const result = await authenticateExternal('john_doe', 'secret');

    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(result).toEqual({
      ok: false,
      authenticated: false,
      reason: 'UPSTREAM_UNREACHABLE'
    });
  });
});
