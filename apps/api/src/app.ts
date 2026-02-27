import express from 'express';
import { ROLES, type Role, type ThemeMode } from '@printo/shared';
import { AuthService } from './auth/service.js';
import type { AuthStore } from './store/auth-store.js';
import { requireAuth, requireRole } from './middleware/auth.js';
import type { JsonObject, PrinterType, UserRecord } from './types.js';

const ALLOWED_THEMES: ThemeMode[] = ['system', 'light', 'dark'];
const ALLOWED_ROLES: Role[] = [ROLES.USER, ROLES.ADMIN];
const ALLOWED_PRINTER_TYPES: PrinterType[] = ['A4', 'THERMAL'];

function isJsonObject(value: unknown): value is JsonObject {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function parseStringArray(value: unknown): string[] | null {
  if (!Array.isArray(value)) {
    return null;
  }

  if (!value.every((item) => typeof item === 'string')) {
    return null;
  }

  return value;
}

function toPublicUser(user: UserRecord) {
  return {
    id: user.id,
    username: user.username,
    roles: user.roles,
    locale: user.locale,
    theme: user.theme,
    isRemoteEnabled: user.isRemoteEnabled
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
      const isRemoteEnabled = typeof req.body?.isRemoteEnabled === 'boolean' ? req.body.isRemoteEnabled : false;

      if (!username || !password) {
        return res.status(400).json({ error: 'INVALID_INPUT' });
      }

      const roles = requestedRoles ?? [ROLES.USER];
      const user = await authService.register({ username, password, roles, isRemoteEnabled });
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
      const isRemoteEnabled = typeof req.body?.isRemoteEnabled === 'boolean' ? req.body.isRemoteEnabled : false;

      if (!username || !password) {
        return res.status(400).json({ error: 'INVALID_INPUT' });
      }

      const user = await authService.register({ username, password, roles, isRemoteEnabled });

      await store.writeAuditEvent({
        actorUserId: req.user!.id,
        action: 'ADMIN_USER_CREATED',
        status: 'SUCCESS',
        targetType: 'USER',
        targetId: user.id,
        metadata: { username: user.username, roles: user.roles, isRemoteEnabled: user.isRemoteEnabled }
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

  app.get('/admin/config/smb-sources', requireAuth, requireRole(ROLES.ADMIN), async (_req, res) => {
    const records = await store.listSmbSources();
    return res.json(records);
  });

  app.post('/admin/config/smb-sources', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const path = typeof req.body?.path === 'string' ? req.body.path : '';
    const domainUsername = typeof req.body?.domainUsername === 'string' ? req.body.domainUsername : '';
    const secretRef = typeof req.body?.secretRef === 'string' ? req.body.secretRef : '';
    const ownerUserId = typeof req.body?.ownerUserId === 'string' ? req.body.ownerUserId : null;
    const isActive = typeof req.body?.isActive === 'boolean' ? req.body.isActive : true;

    if (!path || !domainUsername || !secretRef) {
      return res.status(400).json({ error: 'INVALID_INPUT' });
    }

    const created = await store.createSmbSource({ path, domainUsername, secretRef, ownerUserId, isActive });

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_SMB_SOURCE_CREATED',
      status: 'SUCCESS',
      targetType: 'SMB_SOURCE',
      targetId: created.id
    });

    return res.status(201).json(created);
  });

  app.patch('/admin/config/smb-sources/:sourceId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const updates = {
      id: req.params.sourceId,
      ownerUserId:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'ownerUserId')
          ? req.body.ownerUserId === null
            ? null
            : String(req.body.ownerUserId)
          : undefined,
      path: typeof req.body?.path === 'string' ? req.body.path : undefined,
      domainUsername: typeof req.body?.domainUsername === 'string' ? req.body.domainUsername : undefined,
      secretRef: typeof req.body?.secretRef === 'string' ? req.body.secretRef : undefined,
      isActive: typeof req.body?.isActive === 'boolean' ? req.body.isActive : undefined
    };

    const updated = await store.updateSmbSource(updates);
    if (!updated) {
      return res.status(404).json({ error: 'SMB_SOURCE_NOT_FOUND' });
    }

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_SMB_SOURCE_UPDATED',
      status: 'SUCCESS',
      targetType: 'SMB_SOURCE',
      targetId: updated.id
    });

    return res.json(updated);
  });

  app.delete('/admin/config/smb-sources/:sourceId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const deleted = await store.deleteSmbSource(req.params.sourceId);
    if (!deleted) {
      return res.status(404).json({ error: 'SMB_SOURCE_NOT_FOUND' });
    }

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_SMB_SOURCE_DELETED',
      status: 'SUCCESS',
      targetType: 'SMB_SOURCE',
      targetId: req.params.sourceId
    });

    return res.status(204).send();
  });

  app.get('/admin/config/printers', requireAuth, requireRole(ROLES.ADMIN), async (_req, res) => {
    const records = await store.listPrinters();
    return res.json(records);
  });

  app.post('/admin/config/printers', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const name = typeof req.body?.name === 'string' ? req.body.name : '';
    const type = req.body?.type as PrinterType | undefined;
    const targetUri = typeof req.body?.targetUri === 'string' ? req.body.targetUri : '';
    const isActive = typeof req.body?.isActive === 'boolean' ? req.body.isActive : true;

    if (!name || !targetUri || !type || !ALLOWED_PRINTER_TYPES.includes(type)) {
      return res.status(400).json({ error: 'INVALID_INPUT' });
    }

    const created = await store.createPrinter({ name, type, targetUri, isActive });

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_PRINTER_CREATED',
      status: 'SUCCESS',
      targetType: 'PRINTER',
      targetId: created.id
    });

    return res.status(201).json(created);
  });

  app.patch('/admin/config/printers/:printerId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const parsedType = req.body?.type as PrinterType | undefined;
    if (parsedType !== undefined && !ALLOWED_PRINTER_TYPES.includes(parsedType)) {
      return res.status(400).json({ error: 'INVALID_TYPE' });
    }

    const updated = await store.updatePrinter({
      id: req.params.printerId,
      name: typeof req.body?.name === 'string' ? req.body.name : undefined,
      type: parsedType,
      targetUri: typeof req.body?.targetUri === 'string' ? req.body.targetUri : undefined,
      isActive: typeof req.body?.isActive === 'boolean' ? req.body.isActive : undefined
    });

    if (!updated) {
      return res.status(404).json({ error: 'PRINTER_NOT_FOUND' });
    }

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_PRINTER_UPDATED',
      status: 'SUCCESS',
      targetType: 'PRINTER',
      targetId: updated.id
    });

    return res.json(updated);
  });

  app.delete('/admin/config/printers/:printerId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const deleted = await store.deletePrinter(req.params.printerId);
    if (!deleted) {
      return res.status(404).json({ error: 'PRINTER_NOT_FOUND' });
    }

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_PRINTER_DELETED',
      status: 'SUCCESS',
      targetType: 'PRINTER',
      targetId: req.params.printerId
    });

    return res.status(204).send();
  });

  app.get('/admin/config/filename-masks', requireAuth, requireRole(ROLES.ADMIN), async (_req, res) => {
    const records = await store.listFilenameMasks();
    return res.json(records);
  });

  app.post('/admin/config/filename-masks', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const pattern = typeof req.body?.pattern === 'string' ? req.body.pattern : '';
    const ownerUserId = typeof req.body?.ownerUserId === 'string' ? req.body.ownerUserId : null;
    const isRegex = typeof req.body?.isRegex === 'boolean' ? req.body.isRegex : false;
    const isActive = typeof req.body?.isActive === 'boolean' ? req.body.isActive : true;

    if (!pattern) {
      return res.status(400).json({ error: 'INVALID_INPUT' });
    }

    const created = await store.createFilenameMask({ pattern, ownerUserId, isRegex, isActive });

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_FILENAME_MASK_CREATED',
      status: 'SUCCESS',
      targetType: 'FILENAME_MASK',
      targetId: created.id
    });

    return res.status(201).json(created);
  });

  app.patch('/admin/config/filename-masks/:maskId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const updated = await store.updateFilenameMask({
      id: req.params.maskId,
      ownerUserId:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'ownerUserId')
          ? req.body.ownerUserId === null
            ? null
            : String(req.body.ownerUserId)
          : undefined,
      pattern: typeof req.body?.pattern === 'string' ? req.body.pattern : undefined,
      isRegex: typeof req.body?.isRegex === 'boolean' ? req.body.isRegex : undefined,
      isActive: typeof req.body?.isActive === 'boolean' ? req.body.isActive : undefined
    });

    if (!updated) {
      return res.status(404).json({ error: 'FILENAME_MASK_NOT_FOUND' });
    }

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_FILENAME_MASK_UPDATED',
      status: 'SUCCESS',
      targetType: 'FILENAME_MASK',
      targetId: updated.id
    });

    return res.json(updated);
  });

  app.delete('/admin/config/filename-masks/:maskId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const deleted = await store.deleteFilenameMask(req.params.maskId);
    if (!deleted) {
      return res.status(404).json({ error: 'FILENAME_MASK_NOT_FOUND' });
    }

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_FILENAME_MASK_DELETED',
      status: 'SUCCESS',
      targetType: 'FILENAME_MASK',
      targetId: req.params.maskId
    });

    return res.status(204).send();
  });

  app.get('/admin/config/routing-profiles', requireAuth, requireRole(ROLES.ADMIN), async (_req, res) => {
    const records = await store.listRoutingProfiles();
    return res.json(records);
  });

  app.post('/admin/config/routing-profiles', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const name = typeof req.body?.name === 'string' ? req.body.name : '';
    const thermalLabelPatterns = req.body?.thermalLabelPatterns === undefined ? [] : parseStringArray(req.body.thermalLabelPatterns);
    const fallbackPrinterId =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'fallbackPrinterId')
        ? req.body.fallbackPrinterId === null
          ? null
          : String(req.body.fallbackPrinterId)
        : null;

    if (!name || thermalLabelPatterns === null) {
      return res.status(400).json({ error: 'INVALID_INPUT' });
    }

    const created = await store.createRoutingProfile({ name, thermalLabelPatterns, fallbackPrinterId });

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_ROUTING_PROFILE_CREATED',
      status: 'SUCCESS',
      targetType: 'ROUTING_PROFILE',
      targetId: created.id
    });

    return res.status(201).json(created);
  });

  app.patch('/admin/config/routing-profiles/:routingProfileId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const thermalLabelPatterns =
      req.body?.thermalLabelPatterns === undefined ? undefined : parseStringArray(req.body.thermalLabelPatterns);
    if (thermalLabelPatterns === null) {
      return res.status(400).json({ error: 'INVALID_THERMAL_LABEL_PATTERNS' });
    }

    const updated = await store.updateRoutingProfile({
      id: req.params.routingProfileId,
      name: typeof req.body?.name === 'string' ? req.body.name : undefined,
      thermalLabelPatterns,
      fallbackPrinterId:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'fallbackPrinterId')
          ? req.body.fallbackPrinterId === null
            ? null
            : String(req.body.fallbackPrinterId)
          : undefined
    });

    if (!updated) {
      return res.status(404).json({ error: 'ROUTING_PROFILE_NOT_FOUND' });
    }

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_ROUTING_PROFILE_UPDATED',
      status: 'SUCCESS',
      targetType: 'ROUTING_PROFILE',
      targetId: updated.id
    });

    return res.json(updated);
  });

  app.delete('/admin/config/routing-profiles/:routingProfileId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const deleted = await store.deleteRoutingProfile(req.params.routingProfileId);
    if (!deleted) {
      return res.status(404).json({ error: 'ROUTING_PROFILE_NOT_FOUND' });
    }

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_ROUTING_PROFILE_DELETED',
      status: 'SUCCESS',
      targetType: 'ROUTING_PROFILE',
      targetId: req.params.routingProfileId
    });

    return res.status(204).send();
  });

  app.get('/admin/config/ocr/global', requireAuth, requireRole(ROLES.ADMIN), async (_req, res) => {
    const global = await store.getOcrGlobalConfig();
    return res.json(global);
  });

  app.put('/admin/config/ocr/global', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const provider = typeof req.body?.provider === 'string' ? req.body.provider : undefined;
    const config = req.body?.config === undefined ? undefined : isJsonObject(req.body.config) ? req.body.config : null;

    if (config === null) {
      return res.status(400).json({ error: 'INVALID_CONFIG' });
    }

    const updated = await store.updateOcrGlobalConfig({ provider, config });

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_OCR_GLOBAL_UPDATED',
      status: 'SUCCESS',
      targetType: 'OCR_GLOBAL'
    });

    return res.json(updated);
  });

  app.get('/admin/config/ocr/overrides', requireAuth, requireRole(ROLES.ADMIN), async (_req, res) => {
    const records = await store.listOcrUserOverrides();
    return res.json(records);
  });

  app.put('/admin/config/ocr/overrides/:userId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const config = isJsonObject(req.body?.config) ? req.body.config : null;
    const provider =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'provider')
        ? req.body.provider === null
          ? null
          : String(req.body.provider)
        : undefined;

    if (!config) {
      return res.status(400).json({ error: 'INVALID_CONFIG' });
    }

    const updated = await store.upsertOcrUserOverride({ userId: req.params.userId, provider, config });

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_OCR_OVERRIDE_UPSERTED',
      status: 'SUCCESS',
      targetType: 'OCR_OVERRIDE',
      targetId: updated.userId
    });

    return res.json(updated);
  });

  app.delete('/admin/config/ocr/overrides/:userId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const deleted = await store.deleteOcrUserOverride(req.params.userId);
    if (!deleted) {
      return res.status(404).json({ error: 'OCR_OVERRIDE_NOT_FOUND' });
    }

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_OCR_OVERRIDE_DELETED',
      status: 'SUCCESS',
      targetType: 'OCR_OVERRIDE',
      targetId: req.params.userId
    });

    return res.status(204).send();
  });

  return app;
}
