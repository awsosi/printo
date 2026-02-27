import request from 'supertest';
import { describe, expect, it } from 'vitest';
import { createWebApp } from '../src/app.js';

describe('web health', () => {
  it('returns ok', async () => {
    const app = createWebApp();
    const res = await request(app).get('/health');
    expect(res.status).toBe(200);
    expect(res.body.status).toBe('ok');
  });
});
