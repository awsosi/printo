import request from 'supertest';
import { describe, expect, it, vi } from 'vitest';
import { createWebApp } from '../src/app.js';

describe('web app', () => {
  it('returns health payload', async () => {
    const app = createWebApp();
    const res = await request(app).get('/health');

    expect(res.status).toBe(200);
    expect(res.body).toEqual({ service: 'web', status: 'ok' });
  });

  it('renders admin configuration UI with all config sections', async () => {
    const app = createWebApp();
    const res = await request(app).get('/admin/config');

    expect(res.status).toBe(200);
    expect(res.text).toContain('id="loginForm"');
    expect(res.text).toContain('id="smbCreateForm"');
    expect(res.text).toContain('id="printerCreateForm"');
    expect(res.text).toContain('id="userPrinterAssignmentForm"');
    expect(res.text).toContain('id="maskCreateForm"');
    expect(res.text).toContain('id="routingCreateForm"');
    expect(res.text).toContain('id="ocrGlobalForm"');
    expect(res.text).toContain('id="ocrOverrideForm"');
  });

  it('proxies login requests to API', async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({ accessToken: 'token-1', user: { id: 'u-1', username: 'admin' } }), {
        status: 200,
        headers: { 'content-type': 'application/json' }
      })
    );
    const app = createWebApp({ fetchImpl: fetchMock as typeof fetch, apiBaseUrl: 'http://api.internal' });

    const payload = { username: 'admin', password: 'AdminPass123!' };
    const res = await request(app).post('/auth/login').send(payload);

    expect(res.status).toBe(200);
    expect(res.body.accessToken).toBe('token-1');

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://api.internal/auth/login');
    expect(init.method).toBe('POST');
    expect(init.body).toBe(JSON.stringify(payload));
  });

  it('returns locale payload with fallback to en-US keys', async () => {
    const app = createWebApp();
    const res = await request(app).get('/i18n/messages?locale=pl-PL');

    expect(res.status).toBe(200);
    expect(res.body.locale).toBe('pl-PL');
    expect(res.body.messages['settings.title']).toBe('Ustawienia użytkownika');
    expect(res.body.messages['ocr.config']).toBe('OCR config JSON');
  });

  it('falls back to en-US locale file when requested locale is missing', async () => {
    const app = createWebApp();
    const res = await request(app).get('/i18n/messages?locale=de-DE');

    expect(res.status).toBe(200);
    expect(res.body.locale).toBe('en-US');
    expect(res.body.messages['nav.adminConfig']).toBe('Admin configuration');
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

  it('proxies user printer assignment updates to API', async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({ userId: 'u-1', a4PrinterId: 'p-a4', thermalPrinterId: 'p-thermal' }), {
        status: 200,
        headers: { 'content-type': 'application/json' }
      })
    );
    const app = createWebApp({ fetchImpl: fetchMock as typeof fetch, apiBaseUrl: 'http://api.internal' });

    const payload = {
      a4PrinterId: 'p-a4',
      thermalPrinterId: 'p-thermal'
    };

    const res = await request(app)
      .put('/admin/config/user-printer-assignments/u-1')
      .set('authorization', 'Bearer admin-token')
      .send(payload);

    expect(res.status).toBe(200);
    expect(res.body.userId).toBe('u-1');

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://api.internal/admin/config/user-printer-assignments/u-1');
    expect(init.method).toBe('PUT');
    expect(init.body).toBe(JSON.stringify(payload));
  });

  it('proxies preference updates for locale and theme', async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({ locale: 'pl-PL', theme: 'dark' }), {
        status: 200,
        headers: { 'content-type': 'application/json' }
      })
    );
    const app = createWebApp({ fetchImpl: fetchMock as typeof fetch, apiBaseUrl: 'http://api.internal' });

    const payload = {
      locale: 'pl-PL',
      theme: 'dark'
    };

    const res = await request(app)
      .patch('/me/preferences')
      .set('authorization', 'Bearer user-token')
      .send(payload);

    expect(res.status).toBe(200);
    expect(res.body).toEqual(payload);

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://api.internal/me/preferences');
    expect(init.method).toBe('PATCH');
    expect(init.body).toBe(JSON.stringify(payload));
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
