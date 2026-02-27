import request from 'supertest';
import { describe, expect, it, vi } from 'vitest';
import { createWebApp } from '../src/app.js';

describe('web health', () => {
  it('returns ok', async () => {
    const app = createWebApp();
    const res = await request(app).get('/health');
    expect(res.status).toBe(200);
    expect(res.body.status).toBe('ok');
  });

  it('renders admin configuration UI with SMB and printer forms', async () => {
    const app = createWebApp();
    const res = await request(app).get('/admin/config');

    expect(res.status).toBe(200);
    expect(res.text).toContain('SMB sources');
    expect(res.text).toContain('Printers');
    expect(res.text).toContain('id="smbCreateForm"');
    expect(res.text).toContain('id="printerCreateForm"');
  });

  it('proxies SMB source list requests to API', async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify([{ id: 'smb-1', path: '\\\\server\\share' }]), {
        status: 200,
        headers: { 'content-type': 'application/json' }
      })
    );
    const app = createWebApp({ fetchImpl: fetchMock as typeof fetch, apiBaseUrl: 'http://api.internal' });

    const res = await request(app).get('/admin/config/smb-sources').set('x-auth-token', 'test-token');

    expect(res.status).toBe(200);
    expect(res.body).toEqual([{ id: 'smb-1', path: '\\\\server\\share' }]);

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://api.internal/admin/config/smb-sources');
    expect(init.method).toBe('GET');
    expect((init.headers as Record<string, string>).authorization).toBe('Bearer test-token');
  });

  it('proxies printer create requests with payload', async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({ id: 'printer-1', name: 'A4-1' }), {
        status: 201,
        headers: { 'content-type': 'application/json' }
      })
    );
    const app = createWebApp({ fetchImpl: fetchMock as typeof fetch, apiBaseUrl: 'http://api.internal' });

    const payload = {
      name: 'A4-1',
      type: 'A4',
      targetUri: 'ipp://printer.local/queue',
      isActive: true
    };

    const res = await request(app)
      .post('/admin/config/printers')
      .set('authorization', 'Bearer admin-token')
      .send(payload);

    expect(res.status).toBe(201);
    expect(res.body).toEqual({ id: 'printer-1', name: 'A4-1' });

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://api.internal/admin/config/printers');
    expect(init.method).toBe('POST');
    expect(init.body).toBe(JSON.stringify(payload));
    expect((init.headers as Record<string, string>).authorization).toBe('Bearer admin-token');
  });

  it('returns 401 when proxy auth token is missing', async () => {
    const fetchMock = vi.fn();
    const app = createWebApp({ fetchImpl: fetchMock as typeof fetch, apiBaseUrl: 'http://api.internal' });

    const res = await request(app).get('/admin/config/printers');

    expect(res.status).toBe(401);
    expect(res.body.error).toBe('MISSING_AUTH_TOKEN');
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
