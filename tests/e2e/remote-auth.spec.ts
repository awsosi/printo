import request from 'supertest';
import { afterEach, expect, test, vi } from 'vitest';
import { createApiApp } from '../../apps/api/src/app.js';
import { InMemoryAuthStore } from '../../apps/api/src/store/in-memory-auth-store.js';

const originalExtAuthBaseUrl = process.env.EXTAUTH_BASE_URL;
const originalExtAuthApiKey = process.env.EXTAUTH_API_KEY;
const originalExtAuthRetryBaseMs = process.env.EXTAUTH_RETRY_BASE_MS;

afterEach(() => {
  vi.restoreAllMocks();
  process.env.EXTAUTH_BASE_URL = originalExtAuthBaseUrl;
  process.env.EXTAUTH_API_KEY = originalExtAuthApiKey;
  process.env.EXTAUTH_RETRY_BASE_MS = originalExtAuthRetryBaseMs;
});

test('remote user login works via external auth and enforces USER permissions', async () => {
  const store = new InMemoryAuthStore();
  const api = createApiApp(store);

  process.env.EXTAUTH_BASE_URL = 'https://extauth.example';
  process.env.EXTAUTH_API_KEY = 'extauth-key';
  process.env.EXTAUTH_RETRY_BASE_MS = '0';

  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      new Response(
        JSON.stringify({
          success: true,
          authenticated: true,
          error: null,
          user_id: 8001,
          username: 'remote_e2e_user'
        }),
        {
          status: 200,
          headers: { 'content-type': 'application/json' }
        }
      )
    )
  );

  const register = await request(api).post('/auth/register').send({
    username: 'remote_e2e_user',
    password: 'PlaceholderPass123!',
    isRemoteEnabled: true
  });
  expect(register.status).toBe(201);

  const login = await request(api).post('/auth/login').send({
    username: 'remote_e2e_user',
    password: 'RemotePass123!'
  });
  expect(login.status).toBe(200);
  expect(login.body.user.isRemoteEnabled).toBe(true);

  const userToken = login.body.accessToken as string;

  const me = await request(api).get('/me').set('Authorization', `Bearer ${userToken}`);
  expect(me.status).toBe(200);
  expect(me.body.username).toBe('remote_e2e_user');

  const adminAttempt = await request(api).get('/admin/ping').set('Authorization', `Bearer ${userToken}`);
  expect(adminAttempt.status).toBe(403);

  const remoteAttempts = store.auditEvents.filter((event) => event.action === 'AUTH_REMOTE_ATTEMPT');
  expect(remoteAttempts).toHaveLength(1);
  expect(remoteAttempts[0]?.status).toBe('SUCCESS');
});
