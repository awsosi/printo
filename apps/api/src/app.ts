import express from 'express';
import { ROLES, type Role, type ThemeMode } from '@printo/shared';
import { AuthService } from './auth/service.js';
import type { AuthStore } from './store/auth-store.js';
import { requireAuth, requireRole } from './middleware/auth.js';
import type { UserRecord } from './types.js';

const ALLOWED_THEMES: ThemeMode[] = ['system', 'light', 'dark'];
const ALLOWED_ROLES: Role[] = [ROLES.USER, ROLES.ADMIN];

function toPublicUser(user: UserRecord) {
  return {
    id: user.id,
    username: user.username,
    roles: user.roles,
    locale: user.locale,
    theme: user.theme
  };
}

function parseRoles(input: unknown): Role[] | null {
  if (!Array.isArray(input)) {
    return null;
  }

  const normalized = input.filter((candidate): candidate is Role => ALLOWED_ROLES.includes(candidate as Role));
  if (normalized.length === 0) {
    return [ROLES.USER];
  }

  if (normalized.includes(ROLES.ADMIN)) {
    return [ROLES.ADMIN];
  }

  return [ROLES.USER];
}

export function createApiApp(store: AuthStore) {
  const app = express();
  const authService = new AuthService(store);

  app.use(express.json());

  app.get('/health', (_req, res) => {
    res.json({ service: 'api', status: 'ok' });
  });

  app.post('/auth/register', async (req, res) => {
    try {
      const username = String(req.body?.username ?? '');
      const password = String(req.body?.password ?? '');
      const requestedRoles = parseRoles(req.body?.roles);

      if (!username || !password) {
        return res.status(400).json({ error: 'INVALID_INPUT' });
      }

      const roles = requestedRoles ?? [ROLES.USER];
      const user = await authService.register({ username, password, roles });
      return res.status(201).json(user);
    } catch (error) {
      if ((error as Error).message === 'USER_EXISTS') {
        return res.status(409).json({ error: 'USER_EXISTS' });
      }
      return res.status(500).json({ error: 'INTERNAL_ERROR' });
    }
  });

  app.post('/auth/login', async (req, res) => {
    try {
      const username = String(req.body?.username ?? '');
      const password = String(req.body?.password ?? '');
      const result = await authService.login({ username, password });
      return res.json(result);
    } catch (error) {
      if ((error as Error).message === 'INVALID_CREDENTIALS') {
        return res.status(401).json({ error: 'INVALID_CREDENTIALS' });
      }
      return res.status(500).json({ error: 'INTERNAL_ERROR' });
    }
  });

  app.post('/auth/refresh', async (req, res) => {
    try {
      const refreshToken = String(req.body?.refreshToken ?? '');
      const result = await authService.refresh(refreshToken);
      return res.json(result);
    } catch {
      return res.status(401).json({ error: 'INVALID_REFRESH_TOKEN' });
    }
  });

  app.get('/me', requireAuth, async (req, res) => {
    const user = await store.getUserById(req.user!.id);
    return res.json({ id: user?.id, username: user?.username, roles: user?.roles ?? [] });
  });

  app.get('/me/preferences', requireAuth, async (req, res) => {
    const user = await store.getUserById(req.user!.id);
    if (!user) {
      return res.status(404).json({ error: 'USER_NOT_FOUND' });
    }

    return res.json({ locale: user.locale, theme: user.theme });
  });

  app.patch('/me/preferences', requireAuth, async (req, res) => {
    const locale = typeof req.body?.locale === 'string' ? req.body.locale : undefined;
    const theme = typeof req.body?.theme === 'string' ? req.body.theme : undefined;

    if (locale && locale.length < 2) {
      return res.status(400).json({ error: 'INVALID_LOCALE' });
    }

    if (theme && !ALLOWED_THEMES.includes(theme as ThemeMode)) {
      return res.status(400).json({ error: 'INVALID_THEME' });
    }

    const updated = await store.updateUserPreferences({
      userId: req.user!.id,
      locale,
      theme: theme as ThemeMode | undefined
    });

    if (!updated) {
      return res.status(404).json({ error: 'USER_NOT_FOUND' });
    }

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'USER_PREFERENCES_UPDATED',
      status: 'SUCCESS',
      targetType: 'USER',
      targetId: req.user!.id,
      metadata: { locale: updated.locale, theme: updated.theme }
    });

    return res.json({ locale: updated.locale, theme: updated.theme });
  });

  app.get('/admin/ping', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'ADMIN_PING',
      status: 'SUCCESS',
      targetType: 'SYSTEM',
      metadata: { endpoint: '/admin/ping' }
    });
    return res.json({ ok: true, role: 'ADMIN' });
  });

  app.get('/admin/users', requireAuth, requireRole(ROLES.ADMIN), async (_req, res) => {
    const users = await store.listUsers();
    return res.json(users.map(toPublicUser));
  });

  app.post('/admin/users', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    try {
      const username = String(req.body?.username ?? '');
      const password = String(req.body?.password ?? '');
      const roles = parseRoles(req.body?.roles) ?? [ROLES.USER];

      if (!username || !password) {
        return res.status(400).json({ error: 'INVALID_INPUT' });
      }

      const user = await authService.register({ username, password, roles });

      await store.writeAuditEvent({
        actorUserId: req.user!.id,
        action: 'ADMIN_USER_CREATED',
        status: 'SUCCESS',
        targetType: 'USER',
        targetId: user.id,
        metadata: { username: user.username, roles: user.roles }
      });

      return res.status(201).json(user);
    } catch (error) {
      if ((error as Error).message === 'USER_EXISTS') {
        return res.status(409).json({ error: 'USER_EXISTS' });
      }
      return res.status(500).json({ error: 'INTERNAL_ERROR' });
    }
  });

  app.patch('/admin/users/:userId/roles', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const roles = parseRoles(req.body?.roles);
    if (!roles) {
      return res.status(400).json({ error: 'INVALID_ROLES' });
    }

    const updated = await store.setUserRoles({ userId: req.params.userId, roles });
    if (!updated) {
      return res.status(404).json({ error: 'USER_NOT_FOUND' });
    }

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'ADMIN_USER_ROLES_UPDATED',
      status: 'SUCCESS',
      targetType: 'USER',
      targetId: updated.id,
      metadata: { roles: updated.roles }
    });

    return res.json(toPublicUser(updated));
  });

  app.delete('/admin/users/:userId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    if (req.params.userId === req.user!.id) {
      return res.status(400).json({ error: 'SELF_DELETE_NOT_ALLOWED' });
    }

    const deleted = await store.deleteUser(req.params.userId);
    if (!deleted) {
      return res.status(404).json({ error: 'USER_NOT_FOUND' });
    }

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'ADMIN_USER_DELETED',
      status: 'SUCCESS',
      targetType: 'USER',
      targetId: req.params.userId
    });

    return res.status(204).send();
  });

  app.post('/config/printers', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_PRINTER_UPSERT',
      status: 'SUCCESS',
      targetType: 'PRINTER',
      metadata: { payload: req.body }
    });

    return res.status(202).json({
      accepted: true,
      note: 'Skeleton endpoint for config write with audit hook'
    });
  });

  return app;
}
