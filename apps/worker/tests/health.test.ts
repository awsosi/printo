import request from 'supertest';
import { describe, expect, it } from 'vitest';
import { createWorkerApp } from '../src/app.js';

describe('worker app', () => {
  it('returns health', async () => {
    const { app } = createWorkerApp();
    const res = await request(app).get('/health');
    expect(res.status).toBe(200);
    expect(res.body).toEqual({ service: 'worker', status: 'ok' });
  });

  it('runs empty pipeline safely and exposes runner status', async () => {
    const { app } = createWorkerApp();

    const before = await request(app).get('/pipeline/status');
    expect(before.status).toBe(200);
    expect(before.body.runner.runCount).toBe(0);

    const run = await request(app).post('/pipeline/run-once');
    expect(run.status).toBe(200);
    expect(run.body.status).toBe('ok');
    expect(run.body.summary.filesProcessed).toBe(0);

    const after = await request(app).get('/pipeline/status');
    expect(after.status).toBe(200);
    expect(after.body.runner.runCount).toBe(1);
    expect(after.body.runner.lastSummary.filesProcessed).toBe(0);
    expect(after.body.runner.lastError).toBeNull();
  });
});
