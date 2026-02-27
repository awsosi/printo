import request from 'supertest';
import { describe, expect, it } from 'vitest';
import { createApiApp } from '../src/app.js';
import { InMemoryAuthStore } from '../src/store/in-memory-auth-store.js';

async function bootstrapAppWithAdminAndUser() {
  const app = createApiApp(new InMemoryAuthStore());

  await request(app).post('/auth/register').send({
    username: 'admin',
    password: 'AdminPass123!',
    roles: ['ADMIN']
  });

  await request(app).post('/auth/register').send({
    username: 'user',
    password: 'UserPass123!'
  });

  const adminLogin = await request(app).post('/auth/login').send({
    username: 'admin',
    password: 'AdminPass123!'
  });

  const userLogin = await request(app).post('/auth/login').send({
    username: 'user',
    password: 'UserPass123!'
  });

  return {
    app,
    adminToken: adminLogin.body.accessToken as string,
    userToken: userLogin.body.accessToken as string,
    userId: userLogin.body.user.id as string
  };
}

describe('api health', () => {
  it('returns ok', async () => {
    const app = createApiApp(new InMemoryAuthStore());
    const response = await request(app).get('/health');
    expect(response.status).toBe(200);
    expect(response.body.service).toBe('api');
  });
});

describe('auth + rbac', () => {
  it('allows ADMIN route only for admin users', async () => {
    const { app, adminToken, userToken } = await bootstrapAppWithAdminAndUser();

    const adminAllowed = await request(app).get('/admin/ping').set('Authorization', `Bearer ${adminToken}`);
    expect(adminAllowed.status).toBe(200);

    const userDenied = await request(app).get('/admin/ping').set('Authorization', `Bearer ${userToken}`);
    expect(userDenied.status).toBe(403);
  });

  it('issues and refreshes tokens', async () => {
    const app = createApiApp(new InMemoryAuthStore());

    await request(app).post('/auth/register').send({ username: 'u2', password: 'Passw0rd!' });
    const login = await request(app).post('/auth/login').send({ username: 'u2', password: 'Passw0rd!' });

    const refresh = await request(app).post('/auth/refresh').send({ refreshToken: login.body.refreshToken });
    expect(refresh.status).toBe(200);
    expect(typeof refresh.body.accessToken).toBe('string');
    expect(typeof refresh.body.refreshToken).toBe('string');
  });

  it('supports admin user lifecycle operations', async () => {
    const { app, adminToken } = await bootstrapAppWithAdminAndUser();

    const createUser = await request(app)
      .post('/admin/users')
      .set('Authorization', `Bearer ${adminToken}`)
      .send({ username: 'staff1', password: 'StaffPass123!' });
    expect(createUser.status).toBe(201);
    expect(createUser.body.roles).toEqual(['USER']);

    const listUsers = await request(app).get('/admin/users').set('Authorization', `Bearer ${adminToken}`);
    expect(listUsers.status).toBe(200);
    expect(listUsers.body.some((user: { username: string }) => user.username === 'staff1')).toBe(true);

    const promoteUser = await request(app)
      .patch(`/admin/users/${createUser.body.id}/roles`)
      .set('Authorization', `Bearer ${adminToken}`)
      .send({ roles: ['ADMIN'] });
    expect(promoteUser.status).toBe(200);
    expect(promoteUser.body.roles).toEqual(['ADMIN']);

    const deleteUser = await request(app)
      .delete(`/admin/users/${createUser.body.id}`)
      .set('Authorization', `Bearer ${adminToken}`);
    expect(deleteUser.status).toBe(204);
  });

  it('allows user preference updates and blocks admin endpoints for USER role', async () => {
    const app = createApiApp(new InMemoryAuthStore());

    await request(app).post('/auth/register').send({ username: 'u3', password: 'Passw0rd!' });
    const login = await request(app).post('/auth/login').send({ username: 'u3', password: 'Passw0rd!' });

    const updatePreferences = await request(app)
      .patch('/me/preferences')
      .set('Authorization', `Bearer ${login.body.accessToken}`)
      .send({ locale: 'pl-PL', theme: 'dark' });
    expect(updatePreferences.status).toBe(200);
    expect(updatePreferences.body.locale).toBe('pl-PL');
    expect(updatePreferences.body.theme).toBe('dark');

    const readPreferences = await request(app)
      .get('/me/preferences')
      .set('Authorization', `Bearer ${login.body.accessToken}`);
    expect(readPreferences.status).toBe(200);
    expect(readPreferences.body.locale).toBe('pl-PL');
    expect(readPreferences.body.theme).toBe('dark');

    const adminAccessAttempt = await request(app)
      .get('/admin/users')
      .set('Authorization', `Bearer ${login.body.accessToken}`);
    expect(adminAccessAttempt.status).toBe(403);
  });

  it('supports SMB profile CRUD for ADMIN and blocks USER', async () => {
    const { app, adminToken, userToken } = await bootstrapAppWithAdminAndUser();

    const forbidden = await request(app).get('/admin/config/smb-sources').set('Authorization', `Bearer ${userToken}`);
    expect(forbidden.status).toBe(403);

    const created = await request(app)
      .post('/admin/config/smb-sources')
      .set('Authorization', `Bearer ${adminToken}`)
      .send({ path: '/srv/incoming', domainUsername: 'ACME\\svc-print', secretRef: 'vault://printo/smb' });
    expect(created.status).toBe(201);
    expect(created.body.path).toBe('/srv/incoming');

    const listed = await request(app).get('/admin/config/smb-sources').set('Authorization', `Bearer ${adminToken}`);
    expect(listed.status).toBe(200);
    expect(listed.body).toHaveLength(1);

    const patched = await request(app)
      .patch(`/admin/config/smb-sources/${created.body.id}`)
      .set('Authorization', `Bearer ${adminToken}`)
      .send({ isActive: false });
    expect(patched.status).toBe(200);
    expect(patched.body.isActive).toBe(false);

    const deleted = await request(app)
      .delete(`/admin/config/smb-sources/${created.body.id}`)
      .set('Authorization', `Bearer ${adminToken}`);
    expect(deleted.status).toBe(204);
  });

  it('supports printer CRUD for ADMIN and blocks USER', async () => {
    const { app, adminToken, userToken } = await bootstrapAppWithAdminAndUser();

    const forbidden = await request(app).get('/admin/config/printers').set('Authorization', `Bearer ${userToken}`);
    expect(forbidden.status).toBe(403);

    const created = await request(app)
      .post('/admin/config/printers')
      .set('Authorization', `Bearer ${adminToken}`)
      .send({ name: 'A4 Main', type: 'A4', targetUri: 'ipp://printer-a4.local/print' });
    expect(created.status).toBe(201);
    expect(created.body.type).toBe('A4');

    const listed = await request(app).get('/admin/config/printers').set('Authorization', `Bearer ${adminToken}`);
    expect(listed.status).toBe(200);
    expect(listed.body).toHaveLength(1);

    const patched = await request(app)
      .patch(`/admin/config/printers/${created.body.id}`)
      .set('Authorization', `Bearer ${adminToken}`)
      .send({ targetUri: 'ipp://printer-a4.local/new' });
    expect(patched.status).toBe(200);
    expect(patched.body.targetUri).toBe('ipp://printer-a4.local/new');

    const deleted = await request(app)
      .delete(`/admin/config/printers/${created.body.id}`)
      .set('Authorization', `Bearer ${adminToken}`);
    expect(deleted.status).toBe(204);
  });

  it('supports filename mask CRUD for ADMIN and blocks USER', async () => {
    const { app, adminToken, userToken } = await bootstrapAppWithAdminAndUser();

    const forbidden = await request(app)
      .get('/admin/config/filename-masks')
      .set('Authorization', `Bearer ${userToken}`);
    expect(forbidden.status).toBe(403);

    const created = await request(app)
      .post('/admin/config/filename-masks')
      .set('Authorization', `Bearer ${adminToken}`)
      .send({ pattern: '*.pdf', isRegex: false });
    expect(created.status).toBe(201);
    expect(created.body.pattern).toBe('*.pdf');

    const listed = await request(app)
      .get('/admin/config/filename-masks')
      .set('Authorization', `Bearer ${adminToken}`);
    expect(listed.status).toBe(200);
    expect(listed.body).toHaveLength(1);

    const patched = await request(app)
      .patch(`/admin/config/filename-masks/${created.body.id}`)
      .set('Authorization', `Bearer ${adminToken}`)
      .send({ isRegex: true, pattern: '^INV_.*\\.pdf$' });
    expect(patched.status).toBe(200);
    expect(patched.body.isRegex).toBe(true);

    const deleted = await request(app)
      .delete(`/admin/config/filename-masks/${created.body.id}`)
      .set('Authorization', `Bearer ${adminToken}`);
    expect(deleted.status).toBe(204);
  });

  it('supports routing profile CRUD for ADMIN and blocks USER', async () => {
    const { app, adminToken, userToken } = await bootstrapAppWithAdminAndUser();

    const forbidden = await request(app)
      .get('/admin/config/routing-profiles')
      .set('Authorization', `Bearer ${userToken}`);
    expect(forbidden.status).toBe(403);

    const created = await request(app)
      .post('/admin/config/routing-profiles')
      .set('Authorization', `Bearer ${adminToken}`)
      .send({ name: 'default-routing', thermalLabelPatterns: ['LABEL', 'BARCODE'] });
    expect(created.status).toBe(201);
    expect(created.body.thermalLabelPatterns).toEqual(['LABEL', 'BARCODE']);

    const listed = await request(app)
      .get('/admin/config/routing-profiles')
      .set('Authorization', `Bearer ${adminToken}`);
    expect(listed.status).toBe(200);
    expect(listed.body).toHaveLength(1);

    const patched = await request(app)
      .patch(`/admin/config/routing-profiles/${created.body.id}`)
      .set('Authorization', `Bearer ${adminToken}`)
      .send({ thermalLabelPatterns: ['LABEL'] });
    expect(patched.status).toBe(200);
    expect(patched.body.thermalLabelPatterns).toEqual(['LABEL']);

    const deleted = await request(app)
      .delete(`/admin/config/routing-profiles/${created.body.id}`)
      .set('Authorization', `Bearer ${adminToken}`);
    expect(deleted.status).toBe(204);
  });

  it('supports OCR config CRUD for ADMIN and blocks USER', async () => {
    const { app, adminToken, userToken, userId } = await bootstrapAppWithAdminAndUser();

    const forbidden = await request(app)
      .get('/admin/config/ocr/global')
      .set('Authorization', `Bearer ${userToken}`);
    expect(forbidden.status).toBe(403);

    const getGlobal = await request(app)
      .get('/admin/config/ocr/global')
      .set('Authorization', `Bearer ${adminToken}`);
    expect(getGlobal.status).toBe(200);
    expect(getGlobal.body.provider).toBe('mock');

    const updateGlobal = await request(app)
      .put('/admin/config/ocr/global')
      .set('Authorization', `Bearer ${adminToken}`)
      .send({ provider: 'tesseract', config: { language: 'eng' } });
    expect(updateGlobal.status).toBe(200);
    expect(updateGlobal.body.provider).toBe('tesseract');
    expect(updateGlobal.body.config.language).toBe('eng');

    const upsertOverride = await request(app)
      .put(`/admin/config/ocr/overrides/${userId}`)
      .set('Authorization', `Bearer ${adminToken}`)
      .send({ provider: 'mock', config: { profile: 'user-fast' } });
    expect(upsertOverride.status).toBe(200);
    expect(upsertOverride.body.userId).toBe(userId);

    const listOverrides = await request(app)
      .get('/admin/config/ocr/overrides')
      .set('Authorization', `Bearer ${adminToken}`);
    expect(listOverrides.status).toBe(200);
    expect(listOverrides.body).toHaveLength(1);

    const deleteOverride = await request(app)
      .delete(`/admin/config/ocr/overrides/${userId}`)
      .set('Authorization', `Bearer ${adminToken}`);
    expect(deleteOverride.status).toBe(204);
  });
});
