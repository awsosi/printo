import request from 'supertest';
import { describe, expect, it } from 'vitest';
import { createApiApp } from '../src/app.js';
import { InMemoryAuthStore } from '../src/store/in-memory-auth-store.js';

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
    const store = new InMemoryAuthStore();
    const app = createApiApp(store);

    await request(app).post('/auth/register').send({
      username: 'admin1',
      password: 'AdminPass123!',
      roles: ['ADMIN']
    });

    await request(app).post('/auth/register').send({
      username: 'user1',
      password: 'UserPass123!'
    });

    const adminLogin = await request(app).post('/auth/login').send({
      username: 'admin1',
      password: 'AdminPass123!'
    });

    const userLogin = await request(app).post('/auth/login').send({
      username: 'user1',
      password: 'UserPass123!'
    });

    const adminAllowed = await request(app)
      .get('/admin/ping')
      .set('Authorization', `Bearer ${adminLogin.body.accessToken}`);
    expect(adminAllowed.status).toBe(200);

    const userDenied = await request(app)
      .get('/admin/ping')
      .set('Authorization', `Bearer ${userLogin.body.accessToken}`);
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
    const app = createApiApp(new InMemoryAuthStore());

    await request(app).post('/auth/register').send({
      username: 'admin2',
      password: 'AdminPass123!',
      roles: ['ADMIN']
    });

    const adminLogin = await request(app).post('/auth/login').send({
      username: 'admin2',
      password: 'AdminPass123!'
    });

    const createUser = await request(app)
      .post('/admin/users')
      .set('Authorization', `Bearer ${adminLogin.body.accessToken}`)
      .send({ username: 'staff1', password: 'StaffPass123!' });
    expect(createUser.status).toBe(201);
    expect(createUser.body.roles).toEqual(['USER']);

    const listUsers = await request(app)
      .get('/admin/users')
      .set('Authorization', `Bearer ${adminLogin.body.accessToken}`);
    expect(listUsers.status).toBe(200);
    expect(listUsers.body.some((user: { username: string }) => user.username === 'staff1')).toBe(true);

    const promoteUser = await request(app)
      .patch(`/admin/users/${createUser.body.id}/roles`)
      .set('Authorization', `Bearer ${adminLogin.body.accessToken}`)
      .send({ roles: ['ADMIN'] });
    expect(promoteUser.status).toBe(200);
    expect(promoteUser.body.roles).toEqual(['ADMIN']);

    const deleteUser = await request(app)
      .delete(`/admin/users/${createUser.body.id}`)
      .set('Authorization', `Bearer ${adminLogin.body.accessToken}`);
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
});
