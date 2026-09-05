import express from 'express';
import { ROLES, type Role, type ThemeMode } from '@printo/shared';
import { AuthService } from './auth/service.js';
import { discoverDirectorySnapshot } from './ad/directory-sync-adapter.js';
import type { AuthStore } from './store/auth-store.js';
import { requireAuth, requireRole } from './middleware/auth.js';
import { createAgentRouter } from './agents/router.js';
import type { AgentStore } from './agents/store.js';
import { hashPassword } from './auth/password.js';
import type {
  AdDiscoverySnapshot,
  ClassificationRouteRecord,
  JsonObject,
  PageClass,
  PrinterType,
  RoutingVisualRuleRecord,
  UserRecord,
  VisualMatchMode
} from './types.js';

const ALLOWED_THEMES: ThemeMode[] = ['system', 'light', 'dark'];
const ALLOWED_ROLES: Role[] = [ROLES.USER, ROLES.ADMIN];
const ALLOWED_PRINTER_TYPES: PrinterType[] = ['A4', 'THERMAL'];
const ALLOWED_VISUAL_MATCH_MODES: VisualMatchMode[] = ['CONTAINS', 'EXACT'];
const DOMAIN_USERNAME_PATTERN = /^(?:[^\\\s@/]+\\)?[^\\\s@/]+(?:@[^\\\s@/]+\.[^\\\s@/]+)?$/;

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

function parseNullableString(value: unknown): string | null {
  if (value === null) {
    return null;
  }
  if (typeof value === 'string') {
    return value;
  }
  return null;
}

function parseMatchThreshold(value: unknown): number | null {
  const threshold = Number(value);
  if (!Number.isFinite(threshold) || threshold < 0.5 || threshold > 0.9999) {
    return null;
  }
  return threshold;
}

function parseRoutingVisualRules(value: unknown): RoutingVisualRuleRecord[] | null {
  if (!Array.isArray(value)) {
    return null;
  }

  const rules: RoutingVisualRuleRecord[] = [];
  for (const entry of value) {
    if (!isJsonObject(entry) || !isJsonObject(entry.rect)) {
      return null;
    }
    const samplePageNumber = Number(entry.samplePageNumber);
    const x = Number(entry.rect.x);
    const y = Number(entry.rect.y);
    const width = Number(entry.rect.width);
    const height = Number(entry.rect.height);
    if (!Number.isInteger(samplePageNumber) || samplePageNumber < 1) {
      return null;
    }
    if (![x, y, width, height].every((part) => Number.isFinite(part) && part >= 0) || width <= 0 || height <= 0) {
      return null;
    }
    const routeType = entry.routeType === 'THERMAL' ? 'THERMAL' : entry.routeType === 'A4' ? 'A4' : null;
    if (!routeType) {
      return null;
    }
    const matchMode = entry.matchMode === 'EXACT' ? 'EXACT' : entry.matchMode === 'CONTAINS' ? 'CONTAINS' : null;
    if (!matchMode) {
      return null;
    }
    const expectedWords = parseStringArray(entry.expectedWords);
    if (expectedWords === null) {
      return null;
    }
    rules.push({
      id: typeof entry.id === 'string' ? entry.id : '',
      samplePageNumber,
      routeType,
      matchMode,
      expectedText: typeof entry.expectedText === 'string' ? entry.expectedText : '',
      expectedWords,
      rect: { x, y, width, height }
    });
  }

  return rules;
}

const ALLOWED_PAGE_CLASSES: PageClass[] = ['OUTGOING_LABEL_THERMAL', 'RETURN_LABEL_A4', 'DOCUMENT_A4'];

function parseClassificationRoutes(value: unknown): ClassificationRouteRecord[] | null {
  if (!Array.isArray(value)) {
    return null;
  }

  const routes: ClassificationRouteRecord[] = [];
  for (const entry of value) {
    if (!isJsonObject(entry)) {
      return null;
    }
    if (!ALLOWED_PAGE_CLASSES.includes(entry.pageClass as PageClass)) {
      return null;
    }
    const routeType = entry.routeType === 'THERMAL' ? 'THERMAL' : entry.routeType === 'A4' ? 'A4' : null;
    if (!routeType) {
      return null;
    }
    const printerId = entry.printerId === undefined || entry.printerId === null ? null : String(entry.printerId);
    const minConfidence = entry.minConfidence === undefined ? 0 : Number(entry.minConfidence);
    if (!Number.isFinite(minConfidence) || minConfidence < 0 || minConfidence > 1) {
      return null;
    }
    routes.push({
      pageClass: entry.pageClass as PageClass,
      routeType,
      printerId,
      minConfidence
    });
  }

  return routes;
}

function inferPrinterType(name: string): PrinterType {
  return name.toLowerCase().includes('thermal') ? 'THERMAL' : 'A4';
}

function isValidDomainUsername(value: string): boolean {
  return DOMAIN_USERNAME_PATTERN.test(value.trim());
}

function coerceSecretRef(input: { secretRef?: string; password?: string }): string {
  const password = (input.password ?? '').trim();
  if (password) {
    return `plain:${password}`;
  }
  return (input.secretRef ?? '').trim();
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

export function createApiApp(store: AuthStore, agentStore?: AgentStore) {
  const app = express();
  const authService = new AuthService(store);

  app.use(express.json({ limit: '15mb' }));

  // The Windows agent fleet. Optional so the existing tests and the SMB-only deployments that
  // never enrol an agent keep working unchanged.
  if (agentStore) {
    app.use(createAgentRouter(agentStore));
  }

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

      const users = await store.listUsers();
      const isBootstrapState = users.length === 0;
      const wantsAdmin = requestedRoles?.includes(ROLES.ADMIN) ?? false;
      if (wantsAdmin && !isBootstrapState) {
        return res.status(403).json({ error: 'ADMIN_REGISTRATION_FORBIDDEN' });
      }

      // Public registration never grants admin once bootstrap is complete.
      const roles = wantsAdmin && isBootstrapState ? [ROLES.ADMIN] : [ROLES.USER];
      const user = await authService.register({ username, password, roles, isRemoteEnabled });
      return res.status(201).json(user);
    } catch (error) {
      if ((error as Error).message === 'USER_EXISTS') {
        return res.status(409).json({ error: 'USER_EXISTS' });
      }
      return res.status(500).json({ error: 'INTERNAL_ERROR' });
    }
  });

  app.get('/auth/bootstrap-status', async (_req, res) => {
    const users = await store.listUsers();
    return res.json({
      requiresBootstrap: users.length === 0
    });
  });

  app.post('/auth/bootstrap-admin', async (req, res) => {
    try {
      const users = await store.listUsers();
      if (users.length > 0) {
        return res.status(409).json({ error: 'BOOTSTRAP_ALREADY_COMPLETED' });
      }

      const configuredToken = process.env.BOOTSTRAP_ADMIN_TOKEN;
      if (configuredToken) {
        const providedToken = req.header('x-bootstrap-token');
        if (!providedToken || providedToken !== configuredToken) {
          return res.status(403).json({ error: 'INVALID_BOOTSTRAP_TOKEN' });
        }
      }

      const username = String(req.body?.username ?? '');
      const password = String(req.body?.password ?? '');
      const isRemoteEnabled = typeof req.body?.isRemoteEnabled === 'boolean' ? req.body.isRemoteEnabled : false;

      if (!username || !password) {
        return res.status(400).json({ error: 'INVALID_INPUT' });
      }

      const user = await authService.register({
        username,
        password,
        roles: [ROLES.ADMIN],
        isRemoteEnabled
      });

      await store.writeAuditEvent({
        actorUserId: user.id,
        action: 'AUTH_BOOTSTRAP_ADMIN',
        status: 'SUCCESS',
        targetType: 'USER',
        targetId: user.id,
        metadata: { username: user.username }
      });

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

  app.get('/me/printer-assignment', requireAuth, async (req, res) => {
    const assignment = await store.getUserPrinterAssignment(req.user!.id);
    if (!assignment) {
      return res.json({ userId: req.user!.id, a4PrinterId: null, thermalPrinterId: null });
    }

    return res.json(assignment);
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

  app.patch('/admin/users/:userId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const username = typeof req.body?.username === 'string' ? req.body.username : undefined;
    const isRemoteEnabled = typeof req.body?.isRemoteEnabled === 'boolean' ? req.body.isRemoteEnabled : undefined;
    const password = typeof req.body?.password === 'string' ? req.body.password : undefined;

    if (password !== undefined && password.length === 0) {
      return res.status(400).json({ error: 'INVALID_PASSWORD' });
    }

    let passwordHash: string | undefined;
    let hashAlgorithm: string | undefined;
    if (password) {
      const hashed = await hashPassword(password);
      passwordHash = hashed.hash;
      hashAlgorithm = hashed.algorithm;
    }

    const updated = await store.updateUser({
      userId: req.params.userId,
      username,
      isRemoteEnabled,
      passwordHash,
      hashAlgorithm
    });

    if (!updated) {
      return res.status(404).json({ error: 'USER_NOT_FOUND' });
    }

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'ADMIN_USER_UPDATED',
      status: 'SUCCESS',
      targetType: 'USER',
      targetId: updated.id,
      metadata: {
        username: updated.username,
        isRemoteEnabled: updated.isRemoteEnabled,
        passwordUpdated: Boolean(password)
      }
    });

    return res.json(toPublicUser(updated));
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

  app.get('/admin/groups', requireAuth, requireRole(ROLES.ADMIN), async (_req, res) => {
    const groups = await store.listGroups();
    return res.json(groups);
  });

  app.post('/admin/groups', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const name = typeof req.body?.name === 'string' ? req.body.name : '';
    const description = parseNullableString(req.body?.description);
    const isActive = typeof req.body?.isActive === 'boolean' ? req.body.isActive : true;
    if (!name) {
      return res.status(400).json({ error: 'INVALID_INPUT' });
    }

    const created = await store.createGroup({ name, description, isActive });
    return res.status(201).json(created);
  });

  app.patch('/admin/groups/:groupId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const updates = {
      id: req.params.groupId,
      name: typeof req.body?.name === 'string' ? req.body.name : undefined,
      description: req.body && Object.prototype.hasOwnProperty.call(req.body, 'description') ? parseNullableString(req.body.description) : undefined,
      isActive: typeof req.body?.isActive === 'boolean' ? req.body.isActive : undefined
    };
    const updated = await store.updateGroup(updates);
    if (!updated) {
      return res.status(404).json({ error: 'GROUP_NOT_FOUND' });
    }
    return res.json(updated);
  });

  app.delete('/admin/groups/:groupId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const deleted = await store.deleteGroup(req.params.groupId);
    if (!deleted) {
      return res.status(404).json({ error: 'GROUP_NOT_FOUND' });
    }
    return res.status(204).send();
  });

  app.get('/admin/group-memberships', requireAuth, requireRole(ROLES.ADMIN), async (_req, res) => {
    const memberships = await store.listGroupMemberships();
    return res.json(memberships);
  });

  app.post('/admin/group-memberships', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const groupId = typeof req.body?.groupId === 'string' ? req.body.groupId : '';
    const userId = typeof req.body?.userId === 'string' ? req.body.userId : '';
    if (!groupId || !userId) {
      return res.status(400).json({ error: 'INVALID_INPUT' });
    }
    const created = await store.addGroupMembership({ groupId, userId });
    return res.status(201).json(created);
  });

  app.delete('/admin/group-memberships/:groupId/:userId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const deleted = await store.deleteGroupMembership({ groupId: req.params.groupId, userId: req.params.userId });
    if (!deleted) {
      return res.status(404).json({ error: 'GROUP_MEMBERSHIP_NOT_FOUND' });
    }
    return res.status(204).send();
  });

  app.get('/admin/config/ad-sync', requireAuth, requireRole(ROLES.ADMIN), async (_req, res) => {
    const config = await store.getAdSyncConfig();
    return res.json(config);
  });

  app.put('/admin/config/ad-sync', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const enabled = typeof req.body?.enabled === 'boolean' ? req.body.enabled : undefined;
    const serverUrl = typeof req.body?.serverUrl === 'string' ? req.body.serverUrl : undefined;
    const domain = typeof req.body?.domain === 'string' ? req.body.domain : undefined;
    const baseDn = typeof req.body?.baseDn === 'string' ? req.body.baseDn : undefined;
    const bindUsername = typeof req.body?.bindUsername === 'string' ? req.body.bindUsername : undefined;
    const bindSecretRef = typeof req.body?.bindSecretRef === 'string' ? req.body.bindSecretRef : undefined;

    const updated = await store.updateAdSyncConfig({
      enabled,
      serverUrl,
      domain,
      baseDn,
      bindUsername,
      bindSecretRef
    });

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_AD_SYNC_UPDATED',
      status: 'SUCCESS',
      targetType: 'AD_SYNC',
      metadata: { enabled: updated.enabled, serverUrl: updated.serverUrl, domain: updated.domain, baseDn: updated.baseDn }
    });

    return res.json(updated);
  });

  app.post('/admin/config/ad-sync/discover', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    try {
      const config = await store.getAdSyncConfig();
      if (!config.enabled) {
        return res.status(400).json({ error: 'AD_SYNC_DISABLED' });
      }

      const bindPassword = typeof req.body?.bindPassword === 'string' ? req.body.bindPassword : undefined;
      const snapshot = await discoverDirectorySnapshot({ config, bindPassword });
      return res.json(snapshot);
    } catch (error) {
      return res.status(502).json({ error: (error as Error).message || 'AD_SYNC_FAILED' });
    }
  });

  app.post('/admin/config/ad-sync/import', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    try {
      const config = await store.getAdSyncConfig();
      if (!config.enabled) {
        return res.status(400).json({ error: 'AD_SYNC_DISABLED' });
      }

      const bindPassword = typeof req.body?.bindPassword === 'string' ? req.body.bindPassword : undefined;
      const selectedUserIds = parseStringArray(req.body?.userIds) ?? [];
      const selectedGroupIds = parseStringArray(req.body?.groupIds) ?? [];
      const selectedSmbShareIds = parseStringArray(req.body?.smbShareIds) ?? [];
      const selectedPrinterIds = parseStringArray(req.body?.printerIds) ?? [];
      const defaultSmbDomainUsername = typeof req.body?.defaultSmbDomainUsername === 'string' ? req.body.defaultSmbDomainUsername : '';
      const defaultSmbSecretRef =
        typeof req.body?.defaultSmbSecretRef === 'string' ? req.body.defaultSmbSecretRef : config.bindSecretRef || '';

      const snapshot: AdDiscoverySnapshot = await discoverDirectorySnapshot({ config, bindPassword });
      const selectedUsers = snapshot.users.filter((entry) => selectedUserIds.includes(entry.id));
      const selectedGroups = snapshot.groups.filter((entry) => selectedGroupIds.includes(entry.id));
      const selectedSmbShares = snapshot.smbShares.filter((entry) => selectedSmbShareIds.includes(entry.id));
      const selectedPrinters = snapshot.printers.filter((entry) => selectedPrinterIds.includes(entry.id));

      const existingUsers = await store.listUsers();
      const existingUsersByUsername = new Map(existingUsers.map((user) => [user.username.toLowerCase(), { id: user.id, username: user.username }]));

      let createdUsers = 0;
      for (const adUser of selectedUsers) {
        const key = adUser.username.toLowerCase();
        if (existingUsersByUsername.has(key)) {
          continue;
        }
        const generatedPassword = `ad-${Math.random().toString(36).slice(2, 14)}!`;
        const created = await authService.register({
          username: adUser.username,
          password: generatedPassword,
          roles: [ROLES.USER],
          isRemoteEnabled: false
        });
        existingUsersByUsername.set(key, { id: created.id, username: created.username });
        createdUsers += 1;
      }

      const existingGroups = await store.listGroups();
      const existingGroupsByName = new Map(existingGroups.map((group) => [group.name.toLowerCase(), group]));

      let createdGroups = 0;
      for (const adGroup of selectedGroups) {
        const key = adGroup.name.toLowerCase();
        if (existingGroupsByName.has(key)) {
          continue;
        }
        const created = await store.createGroup({
          name: adGroup.name,
          description: `Imported from AD group ${adGroup.name}`,
          isActive: true
        });
        existingGroupsByName.set(key, created);
        createdGroups += 1;
      }

      let createdMemberships = 0;
      for (const adGroup of selectedGroups) {
        const group = existingGroupsByName.get(adGroup.name.toLowerCase());
        if (!group) {
          continue;
        }
        for (const memberUsername of adGroup.memberUsernames) {
          const user = existingUsersByUsername.get(memberUsername.toLowerCase());
          if (!user) {
            continue;
          }
          await store.addGroupMembership({ groupId: group.id, userId: user.id });
          createdMemberships += 1;
        }
      }

      const existingSmbSources = await store.listSmbSources();
      const existingSmbByPath = new Set(existingSmbSources.map((source) => source.path.toLowerCase()));
      let createdSmbSources = 0;
      for (const smb of selectedSmbShares) {
        if (existingSmbByPath.has(smb.path.toLowerCase())) {
          continue;
        }
        await store.createSmbSource({
          path: smb.path,
          domainUsername: smb.domainUsername || defaultSmbDomainUsername || config.bindUsername || 'AD\\svc-printo',
          secretRef: defaultSmbSecretRef || 'env:AD_SERVICE_ACCOUNT_PASSWORD',
          isActive: true
        });
        createdSmbSources += 1;
      }

      const existingPrinters = await store.listPrinters();
      const existingPrinterNames = new Set(existingPrinters.map((printer) => printer.name.toLowerCase()));
      let createdPrinters = 0;
      for (const printer of selectedPrinters) {
        if (existingPrinterNames.has(printer.name.toLowerCase())) {
          continue;
        }

        const fallbackSlug = printer.name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '') || 'printer';
        await store.createPrinter({
          name: printer.name,
          type: printer.type ?? inferPrinterType(printer.name),
          targetUri: printer.targetUri ?? `socket://${fallbackSlug}.local:9100`,
          isActive: true
        });
        createdPrinters += 1;
      }

      await store.writeAuditEvent({
        actorUserId: req.user!.id,
        action: 'CONFIG_AD_SYNC_IMPORTED',
        status: 'SUCCESS',
        targetType: 'AD_SYNC',
        metadata: {
          selectedUsers: selectedUsers.length,
          selectedGroups: selectedGroups.length,
          selectedSmbShares: selectedSmbShares.length,
          selectedPrinters: selectedPrinters.length,
          createdUsers,
          createdGroups,
          createdMemberships,
          createdSmbSources,
          createdPrinters
        }
      });

      return res.json({
        imported: {
          selectedUsers: selectedUsers.length,
          selectedGroups: selectedGroups.length,
          selectedSmbShares: selectedSmbShares.length,
          selectedPrinters: selectedPrinters.length
        },
        created: {
          users: createdUsers,
          groups: createdGroups,
          memberships: createdMemberships,
          smbSources: createdSmbSources,
          printers: createdPrinters
        }
      });
    } catch (error) {
      return res.status(502).json({ error: (error as Error).message || 'AD_SYNC_IMPORT_FAILED' });
    }
  });

  app.get('/admin/config/smb-sources', requireAuth, requireRole(ROLES.ADMIN), async (_req, res) => {
    const records = await store.listSmbSources();
    return res.json(records);
  });

  app.post('/admin/config/smb-sources', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const path = typeof req.body?.path === 'string' ? req.body.path : '';
    const domainUsername = typeof req.body?.domainUsername === 'string' ? req.body.domainUsername : '';
    const secretRef = coerceSecretRef({
      secretRef: typeof req.body?.secretRef === 'string' ? req.body.secretRef : '',
      password: typeof req.body?.password === 'string' ? req.body.password : ''
    });
    const printerDomainUsername = typeof req.body?.printerDomainUsername === 'string' ? req.body.printerDomainUsername : '';
    const printerSecretRef = coerceSecretRef({
      secretRef: typeof req.body?.printerSecretRef === 'string' ? req.body.printerSecretRef : '',
      password: typeof req.body?.printerPassword === 'string' ? req.body.printerPassword : ''
    });
    const ownerUserId = typeof req.body?.ownerUserId === 'string' ? req.body.ownerUserId : null;
    const ownerGroupId = typeof req.body?.ownerGroupId === 'string' ? req.body.ownerGroupId : null;
    const routingProfileId =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'routingProfileId') ? parseNullableString(req.body.routingProfileId) : null;
    const a4PrinterId =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'a4PrinterId') ? parseNullableString(req.body.a4PrinterId) : null;
    const thermalPrinterId =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'thermalPrinterId') ? parseNullableString(req.body.thermalPrinterId) : null;
    const includeFilenamePatterns =
      req.body?.includeFilenamePatterns === undefined ? [] : parseStringArray(req.body.includeFilenamePatterns);
    const excludeFilenamePatterns =
      req.body?.excludeFilenamePatterns === undefined ? [] : parseStringArray(req.body.excludeFilenamePatterns);
    const isActive = typeof req.body?.isActive === 'boolean' ? req.body.isActive : true;

    if (!path || includeFilenamePatterns === null || excludeFilenamePatterns === null) {
      return res.status(400).json({ error: 'INVALID_INPUT' });
    }
    if (domainUsername && !isValidDomainUsername(domainUsername)) {
      return res.status(400).json({ error: 'INVALID_DOMAIN_USERNAME' });
    }
    if (printerDomainUsername && !isValidDomainUsername(printerDomainUsername)) {
      return res.status(400).json({ error: 'INVALID_PRINTER_DOMAIN_USERNAME' });
    }

    const created = await store.createSmbSource({
      path,
      domainUsername,
      secretRef,
      printerDomainUsername,
      printerSecretRef,
      ownerUserId,
      ownerGroupId,
      routingProfileId,
      a4PrinterId,
      thermalPrinterId,
      includeFilenamePatterns,
      excludeFilenamePatterns,
      isActive
    });

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
      ownerGroupId:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'ownerGroupId')
          ? req.body.ownerGroupId === null
            ? null
            : String(req.body.ownerGroupId)
          : undefined,
      path: typeof req.body?.path === 'string' ? req.body.path : undefined,
      domainUsername: typeof req.body?.domainUsername === 'string' ? req.body.domainUsername : undefined,
      secretRef:
        req.body && (Object.prototype.hasOwnProperty.call(req.body, 'secretRef') || Object.prototype.hasOwnProperty.call(req.body, 'password'))
          ? coerceSecretRef({
              secretRef: typeof req.body?.secretRef === 'string' ? req.body.secretRef : '',
              password: typeof req.body?.password === 'string' ? req.body.password : ''
            })
          : undefined,
      printerDomainUsername:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'printerDomainUsername')
          ? typeof req.body?.printerDomainUsername === 'string'
            ? req.body.printerDomainUsername
            : ''
          : undefined,
      printerSecretRef:
        req.body &&
        (Object.prototype.hasOwnProperty.call(req.body, 'printerSecretRef') ||
          Object.prototype.hasOwnProperty.call(req.body, 'printerPassword'))
          ? coerceSecretRef({
              secretRef: typeof req.body?.printerSecretRef === 'string' ? req.body.printerSecretRef : '',
              password: typeof req.body?.printerPassword === 'string' ? req.body.printerPassword : ''
            })
          : undefined,
      routingProfileId:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'routingProfileId')
          ? parseNullableString(req.body.routingProfileId)
          : undefined,
      a4PrinterId:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'a4PrinterId') ? parseNullableString(req.body.a4PrinterId) : undefined,
      thermalPrinterId:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'thermalPrinterId')
          ? parseNullableString(req.body.thermalPrinterId)
          : undefined,
      includeFilenamePatterns:
        req.body?.includeFilenamePatterns === undefined
          ? undefined
          : (parseStringArray(req.body.includeFilenamePatterns) ?? undefined),
      excludeFilenamePatterns:
        req.body?.excludeFilenamePatterns === undefined
          ? undefined
          : (parseStringArray(req.body.excludeFilenamePatterns) ?? undefined),
      isActive: typeof req.body?.isActive === 'boolean' ? req.body.isActive : undefined
    };
    if (updates.domainUsername !== undefined && !isValidDomainUsername(updates.domainUsername)) {
      return res.status(400).json({ error: 'INVALID_DOMAIN_USERNAME' });
    }
    if (updates.printerDomainUsername !== undefined && updates.printerDomainUsername && !isValidDomainUsername(updates.printerDomainUsername)) {
      return res.status(400).json({ error: 'INVALID_PRINTER_DOMAIN_USERNAME' });
    }
    if (updates.includeFilenamePatterns === null || updates.excludeFilenamePatterns === null) {
      return res.status(400).json({ error: 'INVALID_FILENAME_PATTERNS' });
    }

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
    const domainUsername = typeof req.body?.domainUsername === 'string' ? req.body.domainUsername : '';
    const secretRef = coerceSecretRef({
      secretRef: typeof req.body?.secretRef === 'string' ? req.body.secretRef : '',
      password: typeof req.body?.password === 'string' ? req.body.password : ''
    });
    const isActive = typeof req.body?.isActive === 'boolean' ? req.body.isActive : true;

    if (!name || !targetUri || !type || !ALLOWED_PRINTER_TYPES.includes(type)) {
      return res.status(400).json({ error: 'INVALID_INPUT' });
    }
    if (domainUsername && !isValidDomainUsername(domainUsername)) {
      return res.status(400).json({ error: 'INVALID_DOMAIN_USERNAME' });
    }

    const created = await store.createPrinter({ name, type, targetUri, domainUsername, secretRef, isActive });

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
    const parsedDomainUsername = typeof req.body?.domainUsername === 'string' ? req.body.domainUsername : undefined;
    if (parsedType !== undefined && !ALLOWED_PRINTER_TYPES.includes(parsedType)) {
      return res.status(400).json({ error: 'INVALID_TYPE' });
    }
    if (parsedDomainUsername !== undefined && parsedDomainUsername && !isValidDomainUsername(parsedDomainUsername)) {
      return res.status(400).json({ error: 'INVALID_DOMAIN_USERNAME' });
    }

    const updated = await store.updatePrinter({
      id: req.params.printerId,
      name: typeof req.body?.name === 'string' ? req.body.name : undefined,
      type: parsedType,
      targetUri: typeof req.body?.targetUri === 'string' ? req.body.targetUri : undefined,
      domainUsername: parsedDomainUsername,
      secretRef:
        req.body && (Object.prototype.hasOwnProperty.call(req.body, 'secretRef') || Object.prototype.hasOwnProperty.call(req.body, 'password'))
          ? coerceSecretRef({
              secretRef: typeof req.body?.secretRef === 'string' ? req.body.secretRef : '',
              password: typeof req.body?.password === 'string' ? req.body.password : ''
            })
          : undefined,
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

  app.get('/admin/config/user-printer-assignments', requireAuth, requireRole(ROLES.ADMIN), async (_req, res) => {
    const records = await store.listUserPrinterAssignments();
    return res.json(records);
  });

  app.put('/admin/config/user-printer-assignments/:userId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const a4PrinterId =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'a4PrinterId')
        ? req.body.a4PrinterId === null
          ? null
          : String(req.body.a4PrinterId)
        : undefined;

    const thermalPrinterId =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'thermalPrinterId')
        ? req.body.thermalPrinterId === null
          ? null
          : String(req.body.thermalPrinterId)
        : undefined;

    const updated = await store.upsertUserPrinterAssignment({
      userId: req.params.userId,
      a4PrinterId,
      thermalPrinterId
    });

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_USER_PRINTER_ASSIGNMENT_UPSERTED',
      status: 'SUCCESS',
      targetType: 'USER_PRINTER_ASSIGNMENT',
      targetId: req.params.userId,
      metadata: { ...updated }
    });

    return res.json(updated);
  });

  app.delete('/admin/config/user-printer-assignments/:userId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const deleted = await store.deleteUserPrinterAssignment(req.params.userId);
    if (!deleted) {
      return res.status(404).json({ error: 'USER_PRINTER_ASSIGNMENT_NOT_FOUND' });
    }

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_USER_PRINTER_ASSIGNMENT_DELETED',
      status: 'SUCCESS',
      targetType: 'USER_PRINTER_ASSIGNMENT',
      targetId: req.params.userId
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
    const ownerGroupId = typeof req.body?.ownerGroupId === 'string' ? req.body.ownerGroupId : null;
    const isRegex = typeof req.body?.isRegex === 'boolean' ? req.body.isRegex : false;
    const isActive = typeof req.body?.isActive === 'boolean' ? req.body.isActive : true;

    if (!pattern) {
      return res.status(400).json({ error: 'INVALID_INPUT' });
    }

    const created = await store.createFilenameMask({ pattern, ownerUserId, ownerGroupId, isRegex, isActive });

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
      ownerGroupId:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'ownerGroupId')
          ? req.body.ownerGroupId === null
            ? null
            : String(req.body.ownerGroupId)
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
    const ownerUserId = typeof req.body?.ownerUserId === 'string' ? req.body.ownerUserId : null;
    const ownerGroupId = typeof req.body?.ownerGroupId === 'string' ? req.body.ownerGroupId : null;
    const printerDomainUsername = typeof req.body?.printerDomainUsername === 'string' ? req.body.printerDomainUsername : '';
    const printerSecretRef = coerceSecretRef({
      secretRef: typeof req.body?.printerSecretRef === 'string' ? req.body.printerSecretRef : '',
      password: typeof req.body?.printerPassword === 'string' ? req.body.printerPassword : ''
    });
    const defaultRouteType =
      req.body?.defaultRouteType === 'THERMAL'
        ? 'THERMAL'
        : req.body?.defaultRouteType === 'A4'
          ? 'A4'
          : 'A4';
    const thermalLabelPatterns = req.body?.thermalLabelPatterns === undefined ? [] : parseStringArray(req.body.thermalLabelPatterns);
    const visualRules = req.body?.visualRules === undefined ? [] : parseRoutingVisualRules(req.body.visualRules);
    const classificationRoutes =
      req.body?.classificationRoutes === undefined ? [] : parseClassificationRoutes(req.body.classificationRoutes);
    const fallbackPrinterId =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'fallbackPrinterId')
        ? req.body.fallbackPrinterId === null
          ? null
          : String(req.body.fallbackPrinterId)
        : null;
    const samplePdfName = req.body?.samplePdfName === undefined ? null : parseNullableString(req.body.samplePdfName);
    const samplePdfBase64 = req.body?.samplePdfBase64 === undefined ? null : parseNullableString(req.body.samplePdfBase64);
    const snippetBase64 = req.body?.snippetBase64 === undefined ? null : parseNullableString(req.body.snippetBase64);
    const matchThreshold = req.body?.matchThreshold === undefined ? 0.88 : parseMatchThreshold(req.body.matchThreshold);

    if (!name || thermalLabelPatterns === null || visualRules === null || matchThreshold === null || classificationRoutes === null) {
      return res.status(400).json({ error: 'INVALID_INPUT' });
    }
    if (printerDomainUsername && !isValidDomainUsername(printerDomainUsername)) {
      return res.status(400).json({ error: 'INVALID_PRINTER_DOMAIN_USERNAME' });
    }

    const created = await store.createRoutingProfile({
      name,
      ownerUserId,
      ownerGroupId,
      printerDomainUsername,
      printerSecretRef,
      defaultRouteType,
      thermalLabelPatterns,
      fallbackPrinterId,
      samplePdfName,
      samplePdfBase64,
      snippetBase64,
      matchThreshold,
      visualRules,
      classificationRoutes
    });

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
    const visualRules = req.body?.visualRules === undefined ? undefined : parseRoutingVisualRules(req.body.visualRules);
    const matchThreshold = req.body?.matchThreshold === undefined ? undefined : parseMatchThreshold(req.body.matchThreshold);
    const classificationRoutes =
      req.body?.classificationRoutes === undefined ? undefined : parseClassificationRoutes(req.body.classificationRoutes);
    if (thermalLabelPatterns === null) {
      return res.status(400).json({ error: 'INVALID_THERMAL_LABEL_PATTERNS' });
    }
    if (visualRules === null) {
      return res.status(400).json({ error: 'INVALID_VISUAL_RULES' });
    }
    if (classificationRoutes === null) {
      return res.status(400).json({ error: 'INVALID_CLASSIFICATION_ROUTES' });
    }
    if (matchThreshold === null) {
      return res.status(400).json({ error: 'INVALID_MATCH_THRESHOLD' });
    }
    if (
      req.body &&
      Object.prototype.hasOwnProperty.call(req.body, 'printerDomainUsername') &&
      typeof req.body.printerDomainUsername === 'string' &&
      req.body.printerDomainUsername &&
      !isValidDomainUsername(req.body.printerDomainUsername)
    ) {
      return res.status(400).json({ error: 'INVALID_PRINTER_DOMAIN_USERNAME' });
    }

    const updated = await store.updateRoutingProfile({
      id: req.params.routingProfileId,
      name: typeof req.body?.name === 'string' ? req.body.name : undefined,
      ownerUserId:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'ownerUserId')
          ? req.body.ownerUserId === null
            ? null
            : String(req.body.ownerUserId)
          : undefined,
      ownerGroupId:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'ownerGroupId')
          ? req.body.ownerGroupId === null
            ? null
            : String(req.body.ownerGroupId)
          : undefined,
      printerDomainUsername:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'printerDomainUsername')
          ? typeof req.body?.printerDomainUsername === 'string'
            ? req.body.printerDomainUsername
            : ''
          : undefined,
      printerSecretRef:
        req.body &&
        (Object.prototype.hasOwnProperty.call(req.body, 'printerSecretRef') ||
          Object.prototype.hasOwnProperty.call(req.body, 'printerPassword'))
          ? coerceSecretRef({
              secretRef: typeof req.body?.printerSecretRef === 'string' ? req.body.printerSecretRef : '',
              password: typeof req.body?.printerPassword === 'string' ? req.body.printerPassword : ''
            })
          : undefined,
      defaultRouteType:
        req.body?.defaultRouteType === 'THERMAL'
          ? 'THERMAL'
          : req.body?.defaultRouteType === 'A4'
            ? 'A4'
            : undefined,
      thermalLabelPatterns,
      visualRules,
      classificationRoutes,
      fallbackPrinterId:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'fallbackPrinterId')
          ? req.body.fallbackPrinterId === null
            ? null
            : String(req.body.fallbackPrinterId)
          : undefined,
      samplePdfName:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'samplePdfName')
          ? parseNullableString(req.body.samplePdfName)
          : undefined,
      samplePdfBase64:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'samplePdfBase64')
          ? parseNullableString(req.body.samplePdfBase64)
          : undefined,
      snippetBase64:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'snippetBase64')
          ? parseNullableString(req.body.snippetBase64)
          : undefined,
      matchThreshold
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

  app.get('/admin/config/visual-profiles', requireAuth, requireRole(ROLES.ADMIN), async (_req, res) => {
    const records = await store.listVisualProfiles();
    return res.json(records);
  });

  app.post('/admin/config/visual-profiles', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const name = typeof req.body?.name === 'string' ? req.body.name : '';
    const snippetBase64 = typeof req.body?.snippetBase64 === 'string' ? req.body.snippetBase64 : '';
    const matchMode = typeof req.body?.matchMode === 'string' ? req.body.matchMode : '';
    const routeType = req.body?.routeType as PrinterType | null | undefined;
    const printerId = req.body && Object.prototype.hasOwnProperty.call(req.body, 'printerId') ? parseNullableString(req.body.printerId) : null;
    const ownerUserId =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'ownerUserId') ? parseNullableString(req.body.ownerUserId) : null;
    const ownerGroupId =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'ownerGroupId') ? parseNullableString(req.body.ownerGroupId) : null;
    const labels = req.body?.labels === undefined ? [] : parseStringArray(req.body.labels);
    const isActive = typeof req.body?.isActive === 'boolean' ? req.body.isActive : true;

    if (!name || !snippetBase64 || !ALLOWED_VISUAL_MATCH_MODES.includes(matchMode as VisualMatchMode) || labels === null) {
      return res.status(400).json({ error: 'INVALID_INPUT' });
    }

    if (routeType !== undefined && routeType !== null && !ALLOWED_PRINTER_TYPES.includes(routeType)) {
      return res.status(400).json({ error: 'INVALID_ROUTE_TYPE' });
    }

    const created = await store.createVisualProfile({
      name,
      snippetBase64,
      matchMode: matchMode as VisualMatchMode,
      routeType: routeType ?? null,
      printerId,
      ownerUserId,
      ownerGroupId,
      labels: labels ?? [],
      isActive
    });

    return res.status(201).json(created);
  });

  app.patch('/admin/config/visual-profiles/:visualProfileId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const labels = req.body?.labels === undefined ? undefined : parseStringArray(req.body.labels);
    if (labels === null) {
      return res.status(400).json({ error: 'INVALID_LABELS' });
    }

    const parsedMode = req.body?.matchMode;
    if (
      parsedMode !== undefined &&
      (typeof parsedMode !== 'string' || !ALLOWED_VISUAL_MATCH_MODES.includes(parsedMode as VisualMatchMode))
    ) {
      return res.status(400).json({ error: 'INVALID_MATCH_MODE' });
    }

    const parsedRouteType = req.body?.routeType;
    if (parsedRouteType !== undefined && parsedRouteType !== null && !ALLOWED_PRINTER_TYPES.includes(parsedRouteType)) {
      return res.status(400).json({ error: 'INVALID_ROUTE_TYPE' });
    }

    const updated = await store.updateVisualProfile({
      id: req.params.visualProfileId,
      name: typeof req.body?.name === 'string' ? req.body.name : undefined,
      snippetBase64: typeof req.body?.snippetBase64 === 'string' ? req.body.snippetBase64 : undefined,
      matchMode: typeof parsedMode === 'string' ? (parsedMode as VisualMatchMode) : undefined,
      routeType: parsedRouteType === undefined ? undefined : parsedRouteType === null ? null : (parsedRouteType as PrinterType),
      printerId:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'printerId') ? parseNullableString(req.body.printerId) : undefined,
      ownerUserId:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'ownerUserId')
          ? parseNullableString(req.body.ownerUserId)
          : undefined,
      ownerGroupId:
        req.body && Object.prototype.hasOwnProperty.call(req.body, 'ownerGroupId')
          ? parseNullableString(req.body.ownerGroupId)
          : undefined,
      labels: labels ?? undefined,
      isActive: typeof req.body?.isActive === 'boolean' ? req.body.isActive : undefined
    });

    if (!updated) {
      return res.status(404).json({ error: 'VISUAL_PROFILE_NOT_FOUND' });
    }

    return res.json(updated);
  });

  app.delete('/admin/config/visual-profiles/:visualProfileId', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const deleted = await store.deleteVisualProfile(req.params.visualProfileId);
    if (!deleted) {
      return res.status(404).json({ error: 'VISUAL_PROFILE_NOT_FOUND' });
    }
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

  app.get('/admin/config/system-settings', requireAuth, requireRole(ROLES.ADMIN), async (_req, res) => {
    const settings = await store.getSystemSettings();
    return res.json(settings);
  });

  app.put('/admin/config/system-settings', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const globalSmbDomainUsername =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'globalSmbDomainUsername')
        ? String(req.body.globalSmbDomainUsername ?? '')
        : undefined;
    const globalSmbSecretRef =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'globalSmbSecretRef') ? String(req.body.globalSmbSecretRef ?? '') : undefined;
    const globalPrinterDomainUsername =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'globalPrinterDomainUsername')
        ? String(req.body.globalPrinterDomainUsername ?? '')
        : undefined;
    const globalPrinterSecretRef =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'globalPrinterSecretRef')
        ? String(req.body.globalPrinterSecretRef ?? '')
        : undefined;
    const workerPollIntervalMs =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'workerPollIntervalMs') ? Number(req.body.workerPollIntervalMs) : undefined;
    const smtpEnabled =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'smtpEnabled') ? Boolean(req.body.smtpEnabled) : undefined;
    const smtpHost = req.body && Object.prototype.hasOwnProperty.call(req.body, 'smtpHost') ? String(req.body.smtpHost ?? '') : undefined;
    const smtpPort = req.body && Object.prototype.hasOwnProperty.call(req.body, 'smtpPort') ? Number(req.body.smtpPort) : undefined;
    const smtpSecure =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'smtpSecure') ? Boolean(req.body.smtpSecure) : undefined;
    const smtpUsername =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'smtpUsername') ? String(req.body.smtpUsername ?? '') : undefined;
    const smtpSecretRef =
      req.body && Object.prototype.hasOwnProperty.call(req.body, 'smtpSecretRef') ? String(req.body.smtpSecretRef ?? '') : undefined;
    const smtpFrom = req.body && Object.prototype.hasOwnProperty.call(req.body, 'smtpFrom') ? String(req.body.smtpFrom ?? '') : undefined;
    const smtpTo = req.body && Object.prototype.hasOwnProperty.call(req.body, 'smtpTo') ? parseStringArray(req.body.smtpTo) : undefined;

    if (globalSmbDomainUsername !== undefined && globalSmbDomainUsername && !isValidDomainUsername(globalSmbDomainUsername)) {
      return res.status(400).json({ error: 'INVALID_GLOBAL_SMB_DOMAIN_USERNAME' });
    }

    if (globalPrinterDomainUsername !== undefined && globalPrinterDomainUsername && !isValidDomainUsername(globalPrinterDomainUsername)) {
      return res.status(400).json({ error: 'INVALID_GLOBAL_PRINTER_DOMAIN_USERNAME' });
    }

    if (workerPollIntervalMs !== undefined && (!Number.isFinite(workerPollIntervalMs) || workerPollIntervalMs < 1000)) {
      return res.status(400).json({ error: 'INVALID_WORKER_POLL_INTERVAL_MS' });
    }

    if (smtpPort !== undefined && (!Number.isFinite(smtpPort) || smtpPort < 1 || smtpPort > 65535)) {
      return res.status(400).json({ error: 'INVALID_SMTP_PORT' });
    }

    if (req.body && Object.prototype.hasOwnProperty.call(req.body, 'smtpTo') && smtpTo === null) {
      return res.status(400).json({ error: 'INVALID_SMTP_TO' });
    }

    const updated = await store.updateSystemSettings({
      globalSmbDomainUsername,
      globalSmbSecretRef,
      globalPrinterDomainUsername,
      globalPrinterSecretRef,
      workerPollIntervalMs: workerPollIntervalMs === undefined ? undefined : Math.trunc(workerPollIntervalMs),
      smtpEnabled,
      smtpHost,
      smtpPort: smtpPort === undefined ? undefined : Math.trunc(smtpPort),
      smtpSecure,
      smtpUsername,
      smtpSecretRef,
      smtpFrom,
      smtpTo: smtpTo ?? undefined
    });

    await store.writeAuditEvent({
      actorUserId: req.user!.id,
      action: 'CONFIG_SYSTEM_SETTINGS_UPDATED',
      status: 'SUCCESS',
      targetType: 'SYSTEM_SETTINGS',
      metadata: {
        workerPollIntervalMs: updated.workerPollIntervalMs,
        smtpEnabled: updated.smtpEnabled,
        smtpHost: updated.smtpHost,
        smtpToCount: updated.smtpTo.length
      }
    });

    return res.json(updated);
  });

  app.get('/admin/logs', requireAuth, requireRole(ROLES.ADMIN), async (req, res) => {
    const parsedLimit = Number(req.query.limit ?? 200);
    const limit = Number.isFinite(parsedLimit) ? Math.max(1, Math.min(500, Math.trunc(parsedLimit))) : 200;
    const records = await store.listAuditEvents(limit);
    return res.json(records);
  });

  return app;
}
