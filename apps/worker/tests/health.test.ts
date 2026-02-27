import request from 'supertest';
import { describe, expect, it } from 'vitest';
import { createWorkerApp } from '../src/app.js';

describe('worker health', () => {
  it('returns ok', async () => {
    const app = createWorkerApp();
    const res = await request(app).get('/health');
    expect(res.status).toBe(200);
    expect(res.body.service).toBe('worker');
  });
});
