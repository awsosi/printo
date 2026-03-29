import request from 'supertest';
import { expect, test } from 'vitest';
import { createApiApp } from '../../apps/api/src/app.js';
import { InMemoryAuthStore } from '../../apps/api/src/store/in-memory-auth-store.js';
import { createWorkerApp } from '../../apps/worker/src/app.js';

test('api + worker health endpoints are reachable', async () => {
  const api = createApiApp(new InMemoryAuthStore());
  const { app: worker } = createWorkerApp();

  const apiHealth = await request(api).get('/health');
  expect(apiHealth.status).toBe(200);
  expect(apiHealth.body).toEqual({
    service: 'api',
    status: 'ok'
  });

  const workerHealth = await request(worker).get('/health');
  expect(workerHealth.status).toBe(200);
  expect(workerHealth.body).toEqual({
    service: 'worker',
    status: 'ok'
  });
});
