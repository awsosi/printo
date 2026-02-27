import request from 'supertest';
import { describe, expect, it } from 'vitest';
import { createWorkerApp } from '../src/app.js';

describe('worker app', () => {
  it('returns health', async () => {
    const app = createWorkerApp();
    const res = await request(app).get('/health');
    expect(res.status).toBe(200);
    expect(res.body).toEqual({ service: 'worker', status: 'ok' });
  });

  it('runs empty pipeline safely', async () => {
    const app = createWorkerApp();
    const res = await request(app).post('/pipeline/run-once');

    expect(res.status).toBe(200);
    expect(res.body.status).toBe('ok');
    expect(res.body.summary.filesProcessed).toBe(0);
  });
});
