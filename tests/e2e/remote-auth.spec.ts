import { once } from 'node:events';
import type { AddressInfo } from 'node:net';
import type { Server as HttpServer } from 'node:http';
import { createServer } from 'node:http';
import { expect, test } from '@playwright/test';
import type { Express } from 'express';
import { createApiApp } from '../../apps/api/src/app.js';
import { InMemoryAuthStore } from '../../apps/api/src/store/in-memory-auth-store.js';

async function listen(app: Express): Promise<{ server: HttpServer; baseUrl: string }> {
  const server = app.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address() as AddressInfo;

  return {
    server,
    baseUrl: `http://127.0.0.1:${address.port}`
  };
}

async function listenExternalAuthMock(): Promise<{ server: HttpServer; baseUrl: string }> {
  const server = createServer((req, res) => {
    const url = new URL(req.url ?? '/', 'http://127.0.0.1');

    if (url.pathname !== '/RFM_Auth') {
      res.statusCode = 404;
      res.end('Not Found');
      return;
    }

    const apiKey = url.searchParams.get('ApiKey');
    const username = url.searchParams.get('UserName') ?? '';
    const password = url.searchParams.get('Password') ?? '';

    res.setHeader('content-type', 'application/json');

    if (apiKey !== 'extauth-key') {
      res.end(JSON.stringify({ success: false, authenticated: false, error: 'bad_api_key' }));
      return;
    }

    if (password === 'RemotePass123!') {
      res.end(
        JSON.stringify({
          success: true,
          authenticated: true,
          error: null,
          user_id: 8001,
          username
        })
      );
      return;
    }

    res.end(JSON.stringify({ success: true, authenticated: false, error: 'INVALID_CREDENTIALS' }));
  });

  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address() as AddressInfo;

  return {
    server,
    baseUrl: `http://127.0.0.1:${address.port}`
  };
}

test('remote user login works via external auth and enforces USER permissions', async ({ request }) => {
  const originalExtAuthBaseUrl = process.env.EXTAUTH_BASE_URL;
  const originalExtAuthApiKey = process.env.EXTAUTH_API_KEY;
  const originalExtAuthRetryBaseMs = process.env.EXTAUTH_RETRY_BASE_MS;

  const store = new InMemoryAuthStore();
  const externalAuth = await listenExternalAuthMock();

  process.env.EXTAUTH_BASE_URL = externalAuth.baseUrl;
  process.env.EXTAUTH_API_KEY = 'extauth-key';
  process.env.EXTAUTH_RETRY_BASE_MS = '0';

  const api = await listen(createApiApp(store));

  try {
    const register = await request.post(`${api.baseUrl}/auth/register`, {
      data: {
        username: 'remote_e2e_user',
        password: 'PlaceholderPass123!',
        isRemoteEnabled: true
      }
    });
    expect(register.status()).toBe(201);

    const login = await request.post(`${api.baseUrl}/auth/login`, {
      data: {
        username: 'remote_e2e_user',
        password: 'RemotePass123!'
      }
    });
    expect(login.status()).toBe(200);

    const body = await login.json();
    expect(body.user.isRemoteEnabled).toBe(true);

    const userToken = body.accessToken as string;

    const me = await request.get(`${api.baseUrl}/me`, {
      headers: {
        authorization: `Bearer ${userToken}`
      }
    });
    expect(me.status()).toBe(200);
    const meBody = await me.json();
    expect(meBody.username).toBe('remote_e2e_user');

    const adminAttempt = await request.get(`${api.baseUrl}/admin/ping`, {
      headers: {
        authorization: `Bearer ${userToken}`
      }
    });
    expect(adminAttempt.status()).toBe(403);

    const remoteAttempts = store.auditEvents.filter((event) => event.action === 'AUTH_REMOTE_ATTEMPT');
    expect(remoteAttempts).toHaveLength(1);
    expect(remoteAttempts[0]?.status).toBe('SUCCESS');
  } finally {
    process.env.EXTAUTH_BASE_URL = originalExtAuthBaseUrl;
    process.env.EXTAUTH_API_KEY = originalExtAuthApiKey;
    process.env.EXTAUTH_RETRY_BASE_MS = originalExtAuthRetryBaseMs;

    await Promise.all([
      new Promise<void>((resolve) => api.server.close(() => resolve())),
      new Promise<void>((resolve) => externalAuth.server.close(() => resolve()))
    ]);
  }
});
