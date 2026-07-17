import type { Role } from '@printo/shared';
import type { Pool } from 'pg';
import type {
  AdSyncConfigRecord,
  AuditEvent,
  AuditLogRecord,
  ClassificationRouteRecord,
  FilenameMaskRecord,
  GroupMembershipRecord,
  GroupRecord,
  JsonObject,
  OcrGlobalConfigRecord,
  OcrUserOverrideRecord,
  PrinterRecord,
  PrinterType,
  RefreshTokenRecord,
  RoutingProfileRecord,
  RoutingVisualRuleRecord,
  SmbSourceRecord,
  SystemSettingsRecord,
  UserPrinterAssignmentRecord,
  UserRecord,
  VisualMatchMode,
  VisualProfileRecord
} from '../types.js';
import type { AuthStore } from './auth-store.js';

type UserRow = {
  id: string;
  username: string;
  locale: string;
  theme: string;
  is_remote_enabled: boolean;
  password_hash: string | null;
  algorithm: string | null;
  roles: Role[];
};

type SmbSourceRow = {
  id: string;
  owner_user_id: string | null;
  owner_group_id: string | null;
  path: string;
  domain_username: string;
  secret_ref: string;
  printer_domain_username: string;
  printer_secret_ref: string;
  routing_profile_id: string | null;
  a4_printer_id: string | null;
  thermal_printer_id: string | null;
  include_filename_patterns: unknown;
  exclude_filename_patterns: unknown;
  is_active: boolean;
};

type GroupRow = {
  id: string;
  name: string;
  description: string | null;
  is_active: boolean;
};

type GroupMembershipRow = {
  group_id: string;
  user_id: string;
};

type PrinterRow = {
  id: string;
  name: string;
  type: PrinterType;
  target_uri: string;
  domain_username: string;
  secret_ref: string;
  is_active: boolean;
};

type UserPrinterAssignmentRow = {
  user_id: string;
  a4_printer_id: string | null;
  thermal_printer_id: string | null;
};

type FilenameMaskRow = {
  id: string;
  owner_user_id: string | null;
  owner_group_id: string | null;
  pattern: string;
  is_regex: boolean;
  is_active: boolean;
};

type RoutingProfileRow = {
  id: string;
  name: string;
  owner_user_id: string | null;
  owner_group_id: string | null;
  printer_domain_username: string;
  printer_secret_ref: string;
  default_route_type: PrinterType;
  thermal_label_patterns: unknown;
  fallback_printer_id: string | null;
  sample_pdf_name: string | null;
  sample_pdf_base64: string | null;
  snippet_base64: string | null;
  match_threshold: number;
  visual_rules: unknown;
  classification_routes: unknown;
};

type VisualProfileRow = {
  id: string;
  name: string;
  owner_user_id: string | null;
  owner_group_id: string | null;
  snippet_base64: string;
  match_mode: VisualMatchMode;
  route_type: PrinterType | null;
  printer_id: string | null;
  labels: unknown;
  is_active: boolean;
};

type OcrGlobalRow = {
  provider: string;
  config: unknown;
};

type OcrOverrideRow = {
  user_id: string;
  provider: string | null;
  config: unknown;
};

type AdSyncConfigRow = {
  enabled: boolean;
  server_url: string;
  domain: string;
  base_dn: string;
  bind_username: string;
  bind_secret_ref: string;
};

type SystemSettingsRow = {
  global_smb_domain_username: string;
  global_smb_secret_ref: string;
  global_printer_domain_username: string;
  global_printer_secret_ref: string;
  worker_poll_interval_ms: number;
  smtp_enabled: boolean;
  smtp_host: string;
  smtp_port: number;
  smtp_secure: boolean;
  smtp_username: string;
  smtp_secret_ref: string;
  smtp_from: string;
  smtp_to: unknown;
};

function toJsonObject(value: unknown): JsonObject {
  if (value && typeof value === 'object' && !Array.isArray(value)) {
    return value as JsonObject;
  }

  return {};
}

function toStringArray(value: unknown): string[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.filter((entry): entry is string => typeof entry === 'string');
}

function toRoutingVisualRules(value: unknown): RoutingVisualRuleRecord[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.flatMap((entry) => {
    if (!entry || typeof entry !== 'object') {
      return [];
    }
    const candidate = entry as Record<string, unknown>;
    const rect = candidate.rect;
    if (!rect || typeof rect !== 'object') {
      return [];
    }
    const rectRecord = rect as Record<string, unknown>;
    const expectedWords = toStringArray(candidate.expectedWords);
    const routeType = candidate.routeType === 'THERMAL' ? 'THERMAL' : 'A4';
    const matchMode = candidate.matchMode === 'EXACT' ? 'EXACT' : 'CONTAINS';
    const samplePageNumber = Number(candidate.samplePageNumber);
    const x = Number(rectRecord.x);
    const y = Number(rectRecord.y);
    const width = Number(rectRecord.width);
    const height = Number(rectRecord.height);
    if (!Number.isFinite(samplePageNumber) || samplePageNumber < 1) {
      return [];
    }
    if (![x, y, width, height].every((part) => Number.isFinite(part) && part >= 0) || width <= 0 || height <= 0) {
      return [];
    }
    return [
      {
        id: typeof candidate.id === 'string' ? candidate.id : '',
        samplePageNumber,
        routeType,
        matchMode,
        expectedText: typeof candidate.expectedText === 'string' ? candidate.expectedText : '',
        expectedWords,
        rect: { x, y, width, height }
      }
    ];
  });
}

const PAGE_CLASSES = ['OUTGOING_LABEL_THERMAL', 'RETURN_LABEL_A4', 'DOCUMENT_A4'] as const;

function toClassificationRoutes(value: unknown): ClassificationRouteRecord[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.flatMap((entry) => {
    if (!entry || typeof entry !== 'object') {
      return [];
    }
    const candidate = entry as Record<string, unknown>;
    if (!PAGE_CLASSES.includes(candidate.pageClass as (typeof PAGE_CLASSES)[number])) {
      return [];
    }
    const minConfidence = Number(candidate.minConfidence ?? 0);
    return [
      {
        pageClass: candidate.pageClass as (typeof PAGE_CLASSES)[number],
        routeType: candidate.routeType === 'THERMAL' ? ('THERMAL' as const) : ('A4' as const),
        printerId: typeof candidate.printerId === 'string' ? candidate.printerId : null,
        minConfidence: Number.isFinite(minConfidence) ? Math.min(1, Math.max(0, minConfidence)) : 0
      }
    ];
  });
}

function mapUserRow(row: UserRow): UserRecord {
  return {
    id: row.id,
    username: row.username,
    locale: row.locale,
    theme: row.theme,
    roles: row.roles,
    passwordHash: row.password_hash,
    hashAlgorithm: row.algorithm,
    isRemoteEnabled: row.is_remote_enabled
  };
}

function mapSmbSourceRow(row: SmbSourceRow): SmbSourceRecord {
  return {
    id: row.id,
    ownerUserId: row.owner_user_id,
    ownerGroupId: row.owner_group_id,
    path: row.path,
    domainUsername: row.domain_username,
    secretRef: row.secret_ref,
    printerDomainUsername: row.printer_domain_username,
    printerSecretRef: row.printer_secret_ref,
    routingProfileId: row.routing_profile_id,
    a4PrinterId: row.a4_printer_id,
    thermalPrinterId: row.thermal_printer_id,
    includeFilenamePatterns: toStringArray(row.include_filename_patterns),
    excludeFilenamePatterns: toStringArray(row.exclude_filename_patterns),
    isActive: row.is_active
  };
}

function mapGroupRow(row: GroupRow): GroupRecord {
  return {
    id: row.id,
    name: row.name,
    description: row.description,
    isActive: row.is_active
  };
}

function mapGroupMembershipRow(row: GroupMembershipRow): GroupMembershipRecord {
  return {
    groupId: row.group_id,
    userId: row.user_id
  };
}

function mapPrinterRow(row: PrinterRow): PrinterRecord {
  return {
    id: row.id,
    name: row.name,
    type: row.type,
    targetUri: row.target_uri,
    domainUsername: row.domain_username,
    secretRef: row.secret_ref,
    isActive: row.is_active
  };
}

function mapUserPrinterAssignmentRow(row: UserPrinterAssignmentRow): UserPrinterAssignmentRecord {
  return {
    userId: row.user_id,
    a4PrinterId: row.a4_printer_id,
    thermalPrinterId: row.thermal_printer_id
  };
}

function mapFilenameMaskRow(row: FilenameMaskRow): FilenameMaskRecord {
  return {
    id: row.id,
    ownerUserId: row.owner_user_id,
    ownerGroupId: row.owner_group_id,
    pattern: row.pattern,
    isRegex: row.is_regex,
    isActive: row.is_active
  };
}

function mapRoutingProfileRow(row: RoutingProfileRow): RoutingProfileRecord {
  return {
    id: row.id,
    name: row.name,
    ownerUserId: row.owner_user_id,
    ownerGroupId: row.owner_group_id,
    printerDomainUsername: row.printer_domain_username,
    printerSecretRef: row.printer_secret_ref,
    defaultRouteType: row.default_route_type,
    thermalLabelPatterns: toStringArray(row.thermal_label_patterns),
    fallbackPrinterId: row.fallback_printer_id,
    samplePdfName: row.sample_pdf_name,
    samplePdfBase64: row.sample_pdf_base64,
    snippetBase64: row.snippet_base64,
    matchThreshold: row.match_threshold,
    visualRules: toRoutingVisualRules(row.visual_rules),
    classificationRoutes: toClassificationRoutes(row.classification_routes)
  };
}

function mapVisualProfileRow(row: VisualProfileRow): VisualProfileRecord {
  return {
    id: row.id,
    name: row.name,
    ownerUserId: row.owner_user_id,
    ownerGroupId: row.owner_group_id,
    snippetBase64: row.snippet_base64,
    matchMode: row.match_mode,
    routeType: row.route_type,
    printerId: row.printer_id,
    labels: toStringArray(row.labels),
    isActive: row.is_active
  };
}

function mapOcrGlobalRow(row: OcrGlobalRow): OcrGlobalConfigRecord {
  return {
    provider: row.provider,
    config: toJsonObject(row.config)
  };
}

function mapOcrOverrideRow(row: OcrOverrideRow): OcrUserOverrideRecord {
  return {
    userId: row.user_id,
    provider: row.provider,
    config: toJsonObject(row.config)
  };
}

function mapAdSyncConfigRow(row: AdSyncConfigRow): AdSyncConfigRecord {
  return {
    enabled: row.enabled,
    serverUrl: row.server_url,
    domain: row.domain,
    baseDn: row.base_dn,
    bindUsername: row.bind_username,
    bindSecretRef: row.bind_secret_ref
  };
}

function mapSystemSettingsRow(row: SystemSettingsRow): SystemSettingsRecord {
  return {
    globalSmbDomainUsername: row.global_smb_domain_username,
    globalSmbSecretRef: row.global_smb_secret_ref,
    globalPrinterDomainUsername: row.global_printer_domain_username,
    globalPrinterSecretRef: row.global_printer_secret_ref,
    workerPollIntervalMs: row.worker_poll_interval_ms,
    smtpEnabled: row.smtp_enabled,
    smtpHost: row.smtp_host,
    smtpPort: row.smtp_port,
    smtpSecure: row.smtp_secure,
    smtpUsername: row.smtp_username,
    smtpSecretRef: row.smtp_secret_ref,
    smtpFrom: row.smtp_from,
    smtpTo: toStringArray(row.smtp_to)
  };
}

export class PostgresAuthStore implements AuthStore {
  constructor(private readonly db: Pool) {}

  async getUserByUsername(username: string): Promise<UserRecord | null> {
    const result = await this.db.query<UserRow>(
      `SELECT u.id, u.username, u.locale, u.theme, u.is_remote_enabled, c.password_hash, c.algorithm,
              COALESCE(array_agg(r.role) FILTER (WHERE r.role IS NOT NULL), '{}') AS roles
       FROM users u
       LEFT JOIN user_credentials_local c ON c.user_id = u.id
       LEFT JOIN user_roles r ON r.user_id = u.id
       WHERE lower(u.username) = lower($1)
       GROUP BY u.id, u.is_remote_enabled, c.password_hash, c.algorithm`,
      [username]
    );

    if (!result.rows[0]) {
      return null;
    }

    return mapUserRow(result.rows[0]);
  }

  async getUserById(userId: string): Promise<UserRecord | null> {
    const result = await this.db.query<UserRow>(
      `SELECT u.id, u.username, u.locale, u.theme, u.is_remote_enabled, c.password_hash, c.algorithm,
              COALESCE(array_agg(r.role) FILTER (WHERE r.role IS NOT NULL), '{}') AS roles
       FROM users u
       LEFT JOIN user_credentials_local c ON c.user_id = u.id
       LEFT JOIN user_roles r ON r.user_id = u.id
       WHERE u.id = $1
       GROUP BY u.id, u.is_remote_enabled, c.password_hash, c.algorithm`,
      [userId]
    );

    if (!result.rows[0]) {
      return null;
    }

    return mapUserRow(result.rows[0]);
  }

  async listUsers(): Promise<UserRecord[]> {
    const result = await this.db.query<UserRow>(
      `SELECT u.id, u.username, u.locale, u.theme, u.is_remote_enabled, c.password_hash, c.algorithm,
              COALESCE(array_agg(r.role) FILTER (WHERE r.role IS NOT NULL), '{}') AS roles
       FROM users u
       LEFT JOIN user_credentials_local c ON c.user_id = u.id
       LEFT JOIN user_roles r ON r.user_id = u.id
       GROUP BY u.id, u.is_remote_enabled, c.password_hash, c.algorithm
       ORDER BY u.created_at ASC`
    );

    return result.rows.map(mapUserRow);
  }

  async createUser(input: {
    username: string;
    passwordHash: string;
    hashAlgorithm: string;
    roles: Role[];
    isRemoteEnabled?: boolean;
  }): Promise<UserRecord> {
    const client = await this.db.connect();
    try {
      await client.query('BEGIN');
      const userRes = await client.query<Pick<UserRow, 'id' | 'username' | 'locale' | 'theme' | 'is_remote_enabled'>>(
        `INSERT INTO users(username, is_remote_enabled) VALUES ($1, $2)
         RETURNING id, username, locale, theme, is_remote_enabled`,
        [input.username, input.isRemoteEnabled ?? false]
      );
      const user = userRes.rows[0];

      await client.query(
        `INSERT INTO user_credentials_local(user_id, password_hash, algorithm)
         VALUES ($1, $2, $3)`,
        [user.id, input.passwordHash, input.hashAlgorithm]
      );

      for (const role of input.roles) {
        await client.query('INSERT INTO user_roles(user_id, role) VALUES ($1, $2)', [user.id, role]);
      }

      await client.query('COMMIT');

      return {
        id: user.id,
        username: user.username,
        locale: user.locale,
        theme: user.theme,
        roles: input.roles,
        passwordHash: input.passwordHash,
        hashAlgorithm: input.hashAlgorithm,
        isRemoteEnabled: user.is_remote_enabled
      };
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }

  async updateUser(input: {
    userId: string;
    username?: string;
    isRemoteEnabled?: boolean;
    passwordHash?: string;
    hashAlgorithm?: string;
  }): Promise<UserRecord | null> {
    const client = await this.db.connect();
    try {
      await client.query('BEGIN');

      if (input.username !== undefined || input.isRemoteEnabled !== undefined) {
        const setParts: string[] = [];
        const values: unknown[] = [input.userId];
        let index = 2;

        if (input.username !== undefined) {
          setParts.push(`username = $${index}`);
          values.push(input.username);
          index += 1;
        }

        if (input.isRemoteEnabled !== undefined) {
          setParts.push(`is_remote_enabled = $${index}`);
          values.push(input.isRemoteEnabled);
          index += 1;
        }

        if (setParts.length > 0) {
          await client.query(
            `UPDATE users
             SET ${setParts.join(', ')}, updated_at = NOW()
             WHERE id = $1`,
            values
          );
        }
      }

      if (input.passwordHash !== undefined && input.hashAlgorithm !== undefined) {
        await client.query(
          `INSERT INTO user_credentials_local(user_id, password_hash, algorithm)
           VALUES ($1, $2, $3)
           ON CONFLICT (user_id)
           DO UPDATE SET
             password_hash = EXCLUDED.password_hash,
             algorithm = EXCLUDED.algorithm,
             updated_at = NOW()`,
          [input.userId, input.passwordHash, input.hashAlgorithm]
        );
      }

      await client.query('COMMIT');
      return this.getUserById(input.userId);
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }

  async updateUserPreferences(input: { userId: string; locale?: string; theme?: string }): Promise<UserRecord | null> {
    if (!input.locale && !input.theme) {
      return this.getUserById(input.userId);
    }

    const result = await this.db.query<{ id: string }>(
      `UPDATE users
       SET locale = COALESCE($2, locale),
           theme = COALESCE($3, theme),
           updated_at = NOW()
       WHERE id = $1
       RETURNING id`,
      [input.userId, input.locale ?? null, input.theme ?? null]
    );

    if (!result.rows[0]) {
      return null;
    }

    return this.getUserById(input.userId);
  }

  async setUserRoles(input: { userId: string; roles: Role[] }): Promise<UserRecord | null> {
    const client = await this.db.connect();
    try {
      await client.query('BEGIN');
      const exists = await client.query<{ id: string }>('SELECT id FROM users WHERE id = $1', [input.userId]);
      if (!exists.rows[0]) {
        await client.query('ROLLBACK');
        return null;
      }

      await client.query('DELETE FROM user_roles WHERE user_id = $1', [input.userId]);
      for (const role of input.roles) {
        await client.query('INSERT INTO user_roles(user_id, role) VALUES ($1, $2)', [input.userId, role]);
      }

      await client.query('COMMIT');
      return this.getUserById(input.userId);
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }
  }

  async deleteUser(userId: string): Promise<boolean> {
    const result = await this.db.query('DELETE FROM users WHERE id = $1', [userId]);
    return (result.rowCount ?? 0) > 0;
  }

  async listGroups(): Promise<GroupRecord[]> {
    const result = await this.db.query<GroupRow>(
      `SELECT id, name, description, is_active
       FROM user_groups
       ORDER BY created_at ASC`
    );
    return result.rows.map(mapGroupRow);
  }

  async createGroup(input: { name: string; description?: string | null; isActive: boolean }): Promise<GroupRecord> {
    const result = await this.db.query<GroupRow>(
      `INSERT INTO user_groups(name, description, is_active)
       VALUES ($1, $2, $3)
       RETURNING id, name, description, is_active`,
      [input.name, input.description ?? null, input.isActive]
    );
    return mapGroupRow(result.rows[0]);
  }

  async updateGroup(input: { id: string; name?: string; description?: string | null; isActive?: boolean }): Promise<GroupRecord | null> {
    const setParts: string[] = [];
    const values: unknown[] = [input.id];
    let index = 2;

    if (input.name !== undefined) {
      setParts.push(`name = $${index}`);
      values.push(input.name);
      index += 1;
    }
    if (input.description !== undefined) {
      setParts.push(`description = $${index}`);
      values.push(input.description);
      index += 1;
    }
    if (input.isActive !== undefined) {
      setParts.push(`is_active = $${index}`);
      values.push(input.isActive);
      index += 1;
    }
    if (setParts.length === 0) {
      const current = await this.db.query<GroupRow>(
        `SELECT id, name, description, is_active
         FROM user_groups
         WHERE id = $1
         LIMIT 1`,
        [input.id]
      );
      return current.rows[0] ? mapGroupRow(current.rows[0]) : null;
    }

    const result = await this.db.query<GroupRow>(
      `UPDATE user_groups
       SET ${setParts.join(', ')}, updated_at = NOW()
       WHERE id = $1
       RETURNING id, name, description, is_active`,
      values
    );
    return result.rows[0] ? mapGroupRow(result.rows[0]) : null;
  }

  async deleteGroup(id: string): Promise<boolean> {
    const result = await this.db.query('DELETE FROM user_groups WHERE id = $1', [id]);
    return (result.rowCount ?? 0) > 0;
  }

  async listGroupMemberships(): Promise<GroupMembershipRecord[]> {
    const result = await this.db.query<GroupMembershipRow>(
      `SELECT group_id, user_id
       FROM user_group_memberships
       ORDER BY group_id ASC, user_id ASC`
    );
    return result.rows.map(mapGroupMembershipRow);
  }

  async addGroupMembership(input: { groupId: string; userId: string }): Promise<GroupMembershipRecord> {
    const result = await this.db.query<GroupMembershipRow>(
      `INSERT INTO user_group_memberships(group_id, user_id)
       VALUES ($1, $2)
       ON CONFLICT (group_id, user_id) DO UPDATE SET group_id = EXCLUDED.group_id
       RETURNING group_id, user_id`,
      [input.groupId, input.userId]
    );
    return mapGroupMembershipRow(result.rows[0]);
  }

  async deleteGroupMembership(input: { groupId: string; userId: string }): Promise<boolean> {
    const result = await this.db.query(
      `DELETE FROM user_group_memberships
       WHERE group_id = $1 AND user_id = $2`,
      [input.groupId, input.userId]
    );
    return (result.rowCount ?? 0) > 0;
  }

  private async getSmbSourceById(id: string): Promise<SmbSourceRecord | null> {
    const result = await this.db.query<SmbSourceRow>(
      `SELECT id, owner_user_id, owner_group_id, path, domain_username, secret_ref, printer_domain_username, printer_secret_ref,
              routing_profile_id, a4_printer_id, thermal_printer_id, include_filename_patterns, exclude_filename_patterns, is_active
       FROM smb_sources
       WHERE id = $1`,
      [id]
    );

    if (!result.rows[0]) {
      return null;
    }

    return mapSmbSourceRow(result.rows[0]);
  }

  async listSmbSources(): Promise<SmbSourceRecord[]> {
    const result = await this.db.query<SmbSourceRow>(
      `SELECT id, owner_user_id, owner_group_id, path, domain_username, secret_ref, printer_domain_username, printer_secret_ref,
              routing_profile_id, a4_printer_id, thermal_printer_id, include_filename_patterns, exclude_filename_patterns, is_active
       FROM smb_sources
       ORDER BY created_at ASC`
    );

    return result.rows.map(mapSmbSourceRow);
  }

  async createSmbSource(input: {
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    path: string;
    domainUsername: string;
    secretRef: string;
    printerDomainUsername?: string;
    printerSecretRef?: string;
    routingProfileId?: string | null;
    a4PrinterId?: string | null;
    thermalPrinterId?: string | null;
    includeFilenamePatterns?: string[];
    excludeFilenamePatterns?: string[];
    isActive: boolean;
  }): Promise<SmbSourceRecord> {
    const result = await this.db.query<SmbSourceRow>(
      `INSERT INTO smb_sources(
         owner_user_id, owner_group_id, path, domain_username, secret_ref, printer_domain_username, printer_secret_ref,
         routing_profile_id, a4_printer_id, thermal_printer_id, include_filename_patterns, exclude_filename_patterns, is_active
       )
       VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11::jsonb, $12::jsonb, $13)
       RETURNING id, owner_user_id, owner_group_id, path, domain_username, secret_ref, printer_domain_username, printer_secret_ref,
                 routing_profile_id, a4_printer_id, thermal_printer_id, include_filename_patterns, exclude_filename_patterns, is_active`,
      [
        input.ownerUserId ?? null,
        input.ownerGroupId ?? null,
        input.path,
        input.domainUsername,
        input.secretRef,
        input.printerDomainUsername ?? '',
        input.printerSecretRef ?? '',
        input.routingProfileId ?? null,
        input.a4PrinterId ?? null,
        input.thermalPrinterId ?? null,
        JSON.stringify(input.includeFilenamePatterns ?? []),
        JSON.stringify(input.excludeFilenamePatterns ?? []),
        input.isActive
      ]
    );

    return mapSmbSourceRow(result.rows[0]);
  }

  async updateSmbSource(input: {
    id: string;
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    path?: string;
    domainUsername?: string;
    secretRef?: string;
    printerDomainUsername?: string;
    printerSecretRef?: string;
    routingProfileId?: string | null;
    a4PrinterId?: string | null;
    thermalPrinterId?: string | null;
    includeFilenamePatterns?: string[];
    excludeFilenamePatterns?: string[];
    isActive?: boolean;
  }): Promise<SmbSourceRecord | null> {
    const setParts: string[] = [];
    const values: unknown[] = [input.id];
    let index = 2;

    if (input.ownerUserId !== undefined) {
      setParts.push(`owner_user_id = $${index}`);
      values.push(input.ownerUserId);
      index += 1;
    }

    if (input.ownerGroupId !== undefined) {
      setParts.push(`owner_group_id = $${index}`);
      values.push(input.ownerGroupId);
      index += 1;
    }

    if (input.path !== undefined) {
      setParts.push(`path = $${index}`);
      values.push(input.path);
      index += 1;
    }

    if (input.domainUsername !== undefined) {
      setParts.push(`domain_username = $${index}`);
      values.push(input.domainUsername);
      index += 1;
    }

    if (input.secretRef !== undefined) {
      setParts.push(`secret_ref = $${index}`);
      values.push(input.secretRef);
      index += 1;
    }

    if (input.printerDomainUsername !== undefined) {
      setParts.push(`printer_domain_username = $${index}`);
      values.push(input.printerDomainUsername);
      index += 1;
    }

    if (input.printerSecretRef !== undefined) {
      setParts.push(`printer_secret_ref = $${index}`);
      values.push(input.printerSecretRef);
      index += 1;
    }

    if (input.routingProfileId !== undefined) {
      setParts.push(`routing_profile_id = $${index}`);
      values.push(input.routingProfileId);
      index += 1;
    }

    if (input.a4PrinterId !== undefined) {
      setParts.push(`a4_printer_id = $${index}`);
      values.push(input.a4PrinterId);
      index += 1;
    }

    if (input.thermalPrinterId !== undefined) {
      setParts.push(`thermal_printer_id = $${index}`);
      values.push(input.thermalPrinterId);
      index += 1;
    }

    if (input.includeFilenamePatterns !== undefined) {
      setParts.push(`include_filename_patterns = $${index}::jsonb`);
      values.push(JSON.stringify(input.includeFilenamePatterns));
      index += 1;
    }

    if (input.excludeFilenamePatterns !== undefined) {
      setParts.push(`exclude_filename_patterns = $${index}::jsonb`);
      values.push(JSON.stringify(input.excludeFilenamePatterns));
      index += 1;
    }

    if (input.isActive !== undefined) {
      setParts.push(`is_active = $${index}`);
      values.push(input.isActive);
      index += 1;
    }

    if (setParts.length === 0) {
      return this.getSmbSourceById(input.id);
    }

    const result = await this.db.query<{ id: string }>(
      `UPDATE smb_sources
       SET ${setParts.join(', ')}, updated_at = NOW()
       WHERE id = $1
       RETURNING id`,
      values
    );

    if (!result.rows[0]) {
      return null;
    }

    return this.getSmbSourceById(input.id);
  }

  async deleteSmbSource(id: string): Promise<boolean> {
    const result = await this.db.query('DELETE FROM smb_sources WHERE id = $1', [id]);
    return (result.rowCount ?? 0) > 0;
  }

  private async getPrinterById(id: string): Promise<PrinterRecord | null> {
    const result = await this.db.query<PrinterRow>(
      `SELECT id, name, type, target_uri, domain_username, secret_ref, is_active
       FROM printers
       WHERE id = $1`,
      [id]
    );

    if (!result.rows[0]) {
      return null;
    }

    return mapPrinterRow(result.rows[0]);
  }

  async listPrinters(): Promise<PrinterRecord[]> {
    const result = await this.db.query<PrinterRow>(
      `SELECT id, name, type, target_uri, domain_username, secret_ref, is_active
       FROM printers
       ORDER BY created_at ASC`
    );

    return result.rows.map(mapPrinterRow);
  }

  async createPrinter(input: {
    name: string;
    type: PrinterType;
    targetUri: string;
    domainUsername?: string;
    secretRef?: string;
    isActive: boolean;
  }): Promise<PrinterRecord> {
    const result = await this.db.query<PrinterRow>(
      `INSERT INTO printers(name, type, target_uri, domain_username, secret_ref, is_active)
       VALUES ($1, $2, $3, $4, $5, $6)
       RETURNING id, name, type, target_uri, domain_username, secret_ref, is_active`,
      [input.name, input.type, input.targetUri, input.domainUsername ?? '', input.secretRef ?? '', input.isActive]
    );

    return mapPrinterRow(result.rows[0]);
  }

  async updatePrinter(input: {
    id: string;
    name?: string;
    type?: PrinterType;
    targetUri?: string;
    domainUsername?: string;
    secretRef?: string;
    isActive?: boolean;
  }): Promise<PrinterRecord | null> {
    const setParts: string[] = [];
    const values: unknown[] = [input.id];
    let index = 2;

    if (input.name !== undefined) {
      setParts.push(`name = $${index}`);
      values.push(input.name);
      index += 1;
    }

    if (input.type !== undefined) {
      setParts.push(`type = $${index}`);
      values.push(input.type);
      index += 1;
    }

    if (input.targetUri !== undefined) {
      setParts.push(`target_uri = $${index}`);
      values.push(input.targetUri);
      index += 1;
    }

    if (input.domainUsername !== undefined) {
      setParts.push(`domain_username = $${index}`);
      values.push(input.domainUsername);
      index += 1;
    }

    if (input.secretRef !== undefined) {
      setParts.push(`secret_ref = $${index}`);
      values.push(input.secretRef);
      index += 1;
    }

    if (input.isActive !== undefined) {
      setParts.push(`is_active = $${index}`);
      values.push(input.isActive);
      index += 1;
    }

    if (setParts.length === 0) {
      return this.getPrinterById(input.id);
    }

    const result = await this.db.query<{ id: string }>(
      `UPDATE printers
       SET ${setParts.join(', ')}, updated_at = NOW()
       WHERE id = $1
       RETURNING id`,
      values
    );

    if (!result.rows[0]) {
      return null;
    }

    return this.getPrinterById(input.id);
  }

  async deletePrinter(id: string): Promise<boolean> {
    const result = await this.db.query('DELETE FROM printers WHERE id = $1', [id]);
    return (result.rowCount ?? 0) > 0;
  }

  async listUserPrinterAssignments(): Promise<UserPrinterAssignmentRecord[]> {
    const result = await this.db.query<UserPrinterAssignmentRow>(
      `SELECT a.user_id,
              (array_agg(a.printer_id) FILTER (WHERE p.type = 'A4'))[1] AS a4_printer_id,
              (array_agg(a.printer_id) FILTER (WHERE p.type = 'THERMAL'))[1] AS thermal_printer_id
       FROM user_printer_assignments a
       JOIN printers p ON p.id = a.printer_id
       GROUP BY a.user_id
       ORDER BY a.user_id ASC`
    );

    return result.rows.map(mapUserPrinterAssignmentRow);
  }

  async getUserPrinterAssignment(userId: string): Promise<UserPrinterAssignmentRecord | null> {
    const result = await this.db.query<UserPrinterAssignmentRow>(
      `SELECT a.user_id,
              (array_agg(a.printer_id) FILTER (WHERE p.type = 'A4'))[1] AS a4_printer_id,
              (array_agg(a.printer_id) FILTER (WHERE p.type = 'THERMAL'))[1] AS thermal_printer_id
       FROM user_printer_assignments a
       JOIN printers p ON p.id = a.printer_id
       WHERE a.user_id = $1
       GROUP BY a.user_id
       LIMIT 1`,
      [userId]
    );

    if (!result.rows[0]) {
      return null;
    }

    return mapUserPrinterAssignmentRow(result.rows[0]);
  }

  async upsertUserPrinterAssignment(input: {
    userId: string;
    a4PrinterId?: string | null;
    thermalPrinterId?: string | null;
  }): Promise<UserPrinterAssignmentRecord> {
    const client = await this.db.connect();

    try {
      await client.query('BEGIN');

      const rows = await client.query<{ printer_id: string }>('SELECT printer_id FROM user_printer_assignments WHERE user_id = $1', [
        input.userId
      ]);
      const current = rows.rows.map((row) => row.printer_id);

      const desired = new Set<string>();

      if (input.a4PrinterId === undefined) {
        const existingA4 = await client.query<{ printer_id: string }>(
          `SELECT a.printer_id
           FROM user_printer_assignments a
           JOIN printers p ON p.id = a.printer_id
           WHERE a.user_id = $1 AND p.type = 'A4'
           LIMIT 1`,
          [input.userId]
        );

        if (existingA4.rows[0]) {
          desired.add(existingA4.rows[0].printer_id);
        }
      } else if (input.a4PrinterId) {
        desired.add(input.a4PrinterId);
      }

      if (input.thermalPrinterId === undefined) {
        const existingThermal = await client.query<{ printer_id: string }>(
          `SELECT a.printer_id
           FROM user_printer_assignments a
           JOIN printers p ON p.id = a.printer_id
           WHERE a.user_id = $1 AND p.type = 'THERMAL'
           LIMIT 1`,
          [input.userId]
        );

        if (existingThermal.rows[0]) {
          desired.add(existingThermal.rows[0].printer_id);
        }
      } else if (input.thermalPrinterId) {
        desired.add(input.thermalPrinterId);
      }

      for (const printerId of desired) {
        await client.query(
          `INSERT INTO user_printer_assignments(user_id, printer_id)
           VALUES ($1, $2)
           ON CONFLICT DO NOTHING`,
          [input.userId, printerId]
        );
      }

      for (const existingPrinterId of current) {
        if (!desired.has(existingPrinterId)) {
          await client.query('DELETE FROM user_printer_assignments WHERE user_id = $1 AND printer_id = $2', [
            input.userId,
            existingPrinterId
          ]);
        }
      }

      await client.query('COMMIT');
    } catch (error) {
      await client.query('ROLLBACK');
      throw error;
    } finally {
      client.release();
    }

    return (
      (await this.getUserPrinterAssignment(input.userId)) ?? {
        userId: input.userId,
        a4PrinterId: null,
        thermalPrinterId: null
      }
    );
  }

  async deleteUserPrinterAssignment(userId: string): Promise<boolean> {
    const result = await this.db.query('DELETE FROM user_printer_assignments WHERE user_id = $1', [userId]);
    return (result.rowCount ?? 0) > 0;
  }

  private async getFilenameMaskById(id: string): Promise<FilenameMaskRecord | null> {
    const result = await this.db.query<FilenameMaskRow>(
      `SELECT id, owner_user_id, owner_group_id, pattern, is_regex, is_active
       FROM filename_masks
       WHERE id = $1`,
      [id]
    );

    if (!result.rows[0]) {
      return null;
    }

    return mapFilenameMaskRow(result.rows[0]);
  }

  async listFilenameMasks(): Promise<FilenameMaskRecord[]> {
    const result = await this.db.query<FilenameMaskRow>(
      `SELECT id, owner_user_id, owner_group_id, pattern, is_regex, is_active
       FROM filename_masks
       ORDER BY created_at ASC`
    );

    return result.rows.map(mapFilenameMaskRow);
  }

  async createFilenameMask(input: {
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    pattern: string;
    isRegex: boolean;
    isActive: boolean;
  }): Promise<FilenameMaskRecord> {
    const result = await this.db.query<FilenameMaskRow>(
      `INSERT INTO filename_masks(owner_user_id, owner_group_id, pattern, is_regex, is_active)
       VALUES ($1, $2, $3, $4, $5)
       RETURNING id, owner_user_id, owner_group_id, pattern, is_regex, is_active`,
      [input.ownerUserId ?? null, input.ownerGroupId ?? null, input.pattern, input.isRegex, input.isActive]
    );

    return mapFilenameMaskRow(result.rows[0]);
  }

  async updateFilenameMask(input: {
    id: string;
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    pattern?: string;
    isRegex?: boolean;
    isActive?: boolean;
  }): Promise<FilenameMaskRecord | null> {
    const setParts: string[] = [];
    const values: unknown[] = [input.id];
    let index = 2;

    if (input.ownerUserId !== undefined) {
      setParts.push(`owner_user_id = $${index}`);
      values.push(input.ownerUserId);
      index += 1;
    }

    if (input.ownerGroupId !== undefined) {
      setParts.push(`owner_group_id = $${index}`);
      values.push(input.ownerGroupId);
      index += 1;
    }

    if (input.pattern !== undefined) {
      setParts.push(`pattern = $${index}`);
      values.push(input.pattern);
      index += 1;
    }

    if (input.isRegex !== undefined) {
      setParts.push(`is_regex = $${index}`);
      values.push(input.isRegex);
      index += 1;
    }

    if (input.isActive !== undefined) {
      setParts.push(`is_active = $${index}`);
      values.push(input.isActive);
      index += 1;
    }

    if (setParts.length === 0) {
      return this.getFilenameMaskById(input.id);
    }

    const result = await this.db.query<{ id: string }>(
      `UPDATE filename_masks
       SET ${setParts.join(', ')}
       WHERE id = $1
       RETURNING id`,
      values
    );

    if (!result.rows[0]) {
      return null;
    }

    return this.getFilenameMaskById(input.id);
  }

  async deleteFilenameMask(id: string): Promise<boolean> {
    const result = await this.db.query('DELETE FROM filename_masks WHERE id = $1', [id]);
    return (result.rowCount ?? 0) > 0;
  }

  private async getRoutingProfileById(id: string): Promise<RoutingProfileRecord | null> {
    const result = await this.db.query<RoutingProfileRow>(
      `SELECT id, name, owner_user_id, owner_group_id, printer_domain_username, printer_secret_ref,
              default_route_type, thermal_label_patterns, fallback_printer_id, sample_pdf_name, sample_pdf_base64,
              snippet_base64, match_threshold, visual_rules, classification_routes
       FROM routing_profiles
       WHERE id = $1`,
      [id]
    );

    if (!result.rows[0]) {
      return null;
    }

    return mapRoutingProfileRow(result.rows[0]);
  }

  async listRoutingProfiles(): Promise<RoutingProfileRecord[]> {
    const result = await this.db.query<RoutingProfileRow>(
      `SELECT id, name, owner_user_id, owner_group_id, printer_domain_username, printer_secret_ref,
              default_route_type, thermal_label_patterns, fallback_printer_id, sample_pdf_name, sample_pdf_base64,
              snippet_base64, match_threshold, visual_rules, classification_routes
       FROM routing_profiles
       ORDER BY created_at ASC`
    );

    return result.rows.map(mapRoutingProfileRow);
  }

  async createRoutingProfile(input: {
    name: string;
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    printerDomainUsername?: string;
    printerSecretRef?: string;
    defaultRouteType?: PrinterType;
    thermalLabelPatterns: string[];
    fallbackPrinterId?: string | null;
    samplePdfName?: string | null;
    samplePdfBase64?: string | null;
    snippetBase64?: string | null;
    matchThreshold?: number;
    visualRules?: RoutingVisualRuleRecord[];
    classificationRoutes?: ClassificationRouteRecord[];
  }): Promise<RoutingProfileRecord> {
    const result = await this.db.query<RoutingProfileRow>(
      `INSERT INTO routing_profiles(
         name, owner_user_id, owner_group_id, printer_domain_username, printer_secret_ref, default_route_type,
         thermal_label_patterns, fallback_printer_id, sample_pdf_name, sample_pdf_base64, snippet_base64, match_threshold, visual_rules,
         classification_routes
       )
       VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8, $9, $10, $11, $12, $13::jsonb, $14::jsonb)
       RETURNING id, name, owner_user_id, owner_group_id, printer_domain_username, printer_secret_ref,
                 default_route_type, thermal_label_patterns, fallback_printer_id, sample_pdf_name, sample_pdf_base64,
                 snippet_base64, match_threshold, visual_rules, classification_routes`,
      [
        input.name,
        input.ownerUserId ?? null,
        input.ownerGroupId ?? null,
        input.printerDomainUsername ?? '',
        input.printerSecretRef ?? '',
        input.defaultRouteType ?? 'A4',
        JSON.stringify(input.thermalLabelPatterns),
        input.fallbackPrinterId ?? null,
        input.samplePdfName ?? null,
        input.samplePdfBase64 ?? null,
        input.snippetBase64 ?? null,
        input.matchThreshold ?? 0.88,
        JSON.stringify(input.visualRules ?? []),
        JSON.stringify(input.classificationRoutes ?? [])
      ]
    );

    return mapRoutingProfileRow(result.rows[0]);
  }

  async updateRoutingProfile(input: {
    id: string;
    name?: string;
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    printerDomainUsername?: string;
    printerSecretRef?: string;
    defaultRouteType?: PrinterType;
    thermalLabelPatterns?: string[];
    fallbackPrinterId?: string | null;
    samplePdfName?: string | null;
    samplePdfBase64?: string | null;
    snippetBase64?: string | null;
    matchThreshold?: number;
    visualRules?: RoutingVisualRuleRecord[];
    classificationRoutes?: ClassificationRouteRecord[];
  }): Promise<RoutingProfileRecord | null> {
    const setParts: string[] = [];
    const values: unknown[] = [input.id];
    let index = 2;

    if (input.name !== undefined) {
      setParts.push(`name = $${index}`);
      values.push(input.name);
      index += 1;
    }

    if (input.ownerUserId !== undefined) {
      setParts.push(`owner_user_id = $${index}`);
      values.push(input.ownerUserId);
      index += 1;
    }

    if (input.ownerGroupId !== undefined) {
      setParts.push(`owner_group_id = $${index}`);
      values.push(input.ownerGroupId);
      index += 1;
    }

    if (input.printerDomainUsername !== undefined) {
      setParts.push(`printer_domain_username = $${index}`);
      values.push(input.printerDomainUsername);
      index += 1;
    }

    if (input.printerSecretRef !== undefined) {
      setParts.push(`printer_secret_ref = $${index}`);
      values.push(input.printerSecretRef);
      index += 1;
    }

    if (input.defaultRouteType !== undefined) {
      setParts.push(`default_route_type = $${index}`);
      values.push(input.defaultRouteType);
      index += 1;
    }

    if (input.thermalLabelPatterns !== undefined) {
      setParts.push(`thermal_label_patterns = $${index}::jsonb`);
      values.push(JSON.stringify(input.thermalLabelPatterns));
      index += 1;
    }

    if (input.fallbackPrinterId !== undefined) {
      setParts.push(`fallback_printer_id = $${index}`);
      values.push(input.fallbackPrinterId);
      index += 1;
    }

    if (input.samplePdfName !== undefined) {
      setParts.push(`sample_pdf_name = $${index}`);
      values.push(input.samplePdfName);
      index += 1;
    }

    if (input.samplePdfBase64 !== undefined) {
      setParts.push(`sample_pdf_base64 = $${index}`);
      values.push(input.samplePdfBase64);
      index += 1;
    }

    if (input.snippetBase64 !== undefined) {
      setParts.push(`snippet_base64 = $${index}`);
      values.push(input.snippetBase64);
      index += 1;
    }

    if (input.matchThreshold !== undefined) {
      setParts.push(`match_threshold = $${index}`);
      values.push(input.matchThreshold);
      index += 1;
    }

    if (input.visualRules !== undefined) {
      setParts.push(`visual_rules = $${index}::jsonb`);
      values.push(JSON.stringify(input.visualRules));
      index += 1;
    }

    if (input.classificationRoutes !== undefined) {
      setParts.push(`classification_routes = $${index}::jsonb`);
      values.push(JSON.stringify(input.classificationRoutes));
      index += 1;
    }

    if (setParts.length === 0) {
      return this.getRoutingProfileById(input.id);
    }

    const result = await this.db.query<{ id: string }>(
      `UPDATE routing_profiles
       SET ${setParts.join(', ')}, updated_at = NOW()
       WHERE id = $1
       RETURNING id`,
      values
    );

    if (!result.rows[0]) {
      return null;
    }

    return this.getRoutingProfileById(input.id);
  }

  async deleteRoutingProfile(id: string): Promise<boolean> {
    const result = await this.db.query('DELETE FROM routing_profiles WHERE id = $1', [id]);
    return (result.rowCount ?? 0) > 0;
  }

  private async getVisualProfileById(id: string): Promise<VisualProfileRecord | null> {
    const result = await this.db.query<VisualProfileRow>(
      `SELECT id, name, owner_user_id, owner_group_id, snippet_base64, match_mode, route_type, printer_id, labels, is_active
       FROM visual_match_profiles
       WHERE id = $1`,
      [id]
    );
    if (!result.rows[0]) {
      return null;
    }
    return mapVisualProfileRow(result.rows[0]);
  }

  async listVisualProfiles(): Promise<VisualProfileRecord[]> {
    const result = await this.db.query<VisualProfileRow>(
      `SELECT id, name, owner_user_id, owner_group_id, snippet_base64, match_mode, route_type, printer_id, labels, is_active
       FROM visual_match_profiles
       ORDER BY created_at ASC`
    );
    return result.rows.map(mapVisualProfileRow);
  }

  async createVisualProfile(input: {
    name: string;
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    snippetBase64: string;
    matchMode: VisualMatchMode;
    routeType?: PrinterType | null;
    printerId?: string | null;
    labels?: string[];
    isActive: boolean;
  }): Promise<VisualProfileRecord> {
    const result = await this.db.query<VisualProfileRow>(
      `INSERT INTO visual_match_profiles
         (name, owner_user_id, owner_group_id, snippet_base64, match_mode, route_type, printer_id, labels, is_active)
       VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, $9)
       RETURNING id, name, owner_user_id, owner_group_id, snippet_base64, match_mode, route_type, printer_id, labels, is_active`,
      [
        input.name,
        input.ownerUserId ?? null,
        input.ownerGroupId ?? null,
        input.snippetBase64,
        input.matchMode,
        input.routeType ?? null,
        input.printerId ?? null,
        JSON.stringify(input.labels ?? []),
        input.isActive
      ]
    );
    return mapVisualProfileRow(result.rows[0]);
  }

  async updateVisualProfile(input: {
    id: string;
    name?: string;
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    snippetBase64?: string;
    matchMode?: VisualMatchMode;
    routeType?: PrinterType | null;
    printerId?: string | null;
    labels?: string[];
    isActive?: boolean;
  }): Promise<VisualProfileRecord | null> {
    const setParts: string[] = [];
    const values: unknown[] = [input.id];
    let index = 2;

    if (input.name !== undefined) {
      setParts.push(`name = $${index}`);
      values.push(input.name);
      index += 1;
    }
    if (input.ownerUserId !== undefined) {
      setParts.push(`owner_user_id = $${index}`);
      values.push(input.ownerUserId);
      index += 1;
    }
    if (input.ownerGroupId !== undefined) {
      setParts.push(`owner_group_id = $${index}`);
      values.push(input.ownerGroupId);
      index += 1;
    }
    if (input.snippetBase64 !== undefined) {
      setParts.push(`snippet_base64 = $${index}`);
      values.push(input.snippetBase64);
      index += 1;
    }
    if (input.matchMode !== undefined) {
      setParts.push(`match_mode = $${index}`);
      values.push(input.matchMode);
      index += 1;
    }
    if (input.routeType !== undefined) {
      setParts.push(`route_type = $${index}`);
      values.push(input.routeType);
      index += 1;
    }
    if (input.printerId !== undefined) {
      setParts.push(`printer_id = $${index}`);
      values.push(input.printerId);
      index += 1;
    }
    if (input.labels !== undefined) {
      setParts.push(`labels = $${index}::jsonb`);
      values.push(JSON.stringify(input.labels));
      index += 1;
    }
    if (input.isActive !== undefined) {
      setParts.push(`is_active = $${index}`);
      values.push(input.isActive);
      index += 1;
    }

    if (setParts.length === 0) {
      return this.getVisualProfileById(input.id);
    }

    const result = await this.db.query<{ id: string }>(
      `UPDATE visual_match_profiles
       SET ${setParts.join(', ')}, updated_at = NOW()
       WHERE id = $1
       RETURNING id`,
      values
    );
    if (!result.rows[0]) {
      return null;
    }
    return this.getVisualProfileById(input.id);
  }

  async deleteVisualProfile(id: string): Promise<boolean> {
    const result = await this.db.query('DELETE FROM visual_match_profiles WHERE id = $1', [id]);
    return (result.rowCount ?? 0) > 0;
  }

  async getOcrGlobalConfig(): Promise<OcrGlobalConfigRecord> {
    const result = await this.db.query<OcrGlobalRow>(
      `SELECT provider, config
       FROM ocr_config_global
       WHERE id = TRUE
       LIMIT 1`
    );

    if (!result.rows[0]) {
      return { provider: 'mock', config: {} };
    }

    return mapOcrGlobalRow(result.rows[0]);
  }

  async updateOcrGlobalConfig(input: { provider?: string; config?: JsonObject }): Promise<OcrGlobalConfigRecord> {
    const provider = input.provider ?? null;
    const config = input.config !== undefined ? JSON.stringify(input.config) : null;

    const result = await this.db.query<OcrGlobalRow>(
      `INSERT INTO ocr_config_global(id, provider, config)
       VALUES (TRUE, COALESCE($1, 'mock'), COALESCE($2::jsonb, '{}'::jsonb))
       ON CONFLICT (id)
       DO UPDATE SET
         provider = COALESCE($1, ocr_config_global.provider),
         config = COALESCE($2::jsonb, ocr_config_global.config),
         updated_at = NOW()
       RETURNING provider, config`,
      [provider, config]
    );

    return mapOcrGlobalRow(result.rows[0]);
  }

  async listOcrUserOverrides(): Promise<OcrUserOverrideRecord[]> {
    const result = await this.db.query<OcrOverrideRow>(
      `SELECT user_id, provider, config
       FROM ocr_config_user_override
       ORDER BY user_id ASC`
    );

    return result.rows.map(mapOcrOverrideRow);
  }

  async upsertOcrUserOverride(input: {
    userId: string;
    provider?: string | null;
    config: JsonObject;
  }): Promise<OcrUserOverrideRecord> {
    const shouldUpdateProvider = input.provider !== undefined;

    const result = await this.db.query<OcrOverrideRow>(
      `INSERT INTO ocr_config_user_override(user_id, provider, config)
       VALUES ($1, $2, $3::jsonb)
       ON CONFLICT (user_id)
       DO UPDATE SET
         provider = CASE WHEN $4 THEN EXCLUDED.provider ELSE ocr_config_user_override.provider END,
         config = EXCLUDED.config,
         updated_at = NOW()
       RETURNING user_id, provider, config`,
      [input.userId, input.provider ?? null, JSON.stringify(input.config), shouldUpdateProvider]
    );

    return mapOcrOverrideRow(result.rows[0]);
  }

  async deleteOcrUserOverride(userId: string): Promise<boolean> {
    const result = await this.db.query('DELETE FROM ocr_config_user_override WHERE user_id = $1', [userId]);
    return (result.rowCount ?? 0) > 0;
  }

  async getAdSyncConfig(): Promise<AdSyncConfigRecord> {
    const result = await this.db.query<AdSyncConfigRow>(
      `SELECT enabled, server_url, domain, base_dn, bind_username, bind_secret_ref
       FROM ad_sync_config
       WHERE id = TRUE
       LIMIT 1`
    );

    if (!result.rows[0]) {
      return {
        enabled: false,
        serverUrl: '',
        domain: '',
        baseDn: '',
        bindUsername: '',
        bindSecretRef: ''
      };
    }

    return mapAdSyncConfigRow(result.rows[0]);
  }

  async updateAdSyncConfig(input: {
    enabled?: boolean;
    serverUrl?: string;
    domain?: string;
    baseDn?: string;
    bindUsername?: string;
    bindSecretRef?: string;
  }): Promise<AdSyncConfigRecord> {
    const enabled = input.enabled ?? null;
    const serverUrl = input.serverUrl ?? null;
    const domain = input.domain ?? null;
    const baseDn = input.baseDn ?? null;
    const bindUsername = input.bindUsername ?? null;
    const bindSecretRef = input.bindSecretRef ?? null;

    const result = await this.db.query<AdSyncConfigRow>(
      `INSERT INTO ad_sync_config(id, enabled, server_url, domain, base_dn, bind_username, bind_secret_ref)
       VALUES (TRUE, COALESCE($1, FALSE), COALESCE($2, ''), COALESCE($3, ''), COALESCE($4, ''), COALESCE($5, ''), COALESCE($6, ''))
       ON CONFLICT (id)
       DO UPDATE SET
         enabled = COALESCE($1, ad_sync_config.enabled),
         server_url = COALESCE($2, ad_sync_config.server_url),
         domain = COALESCE($3, ad_sync_config.domain),
         base_dn = COALESCE($4, ad_sync_config.base_dn),
         bind_username = COALESCE($5, ad_sync_config.bind_username),
         bind_secret_ref = COALESCE($6, ad_sync_config.bind_secret_ref),
         updated_at = NOW()
       RETURNING enabled, server_url, domain, base_dn, bind_username, bind_secret_ref`,
      [enabled, serverUrl, domain, baseDn, bindUsername, bindSecretRef]
    );

    return mapAdSyncConfigRow(result.rows[0]);
  }

  async getSystemSettings(): Promise<SystemSettingsRecord> {
    const result = await this.db.query<SystemSettingsRow>(
      `SELECT global_smb_domain_username,
              global_smb_secret_ref,
              global_printer_domain_username,
              global_printer_secret_ref,
              worker_poll_interval_ms,
              smtp_enabled,
              smtp_host,
              smtp_port,
              smtp_secure,
              smtp_username,
              smtp_secret_ref,
              smtp_from,
              smtp_to
       FROM system_settings
       WHERE id = TRUE
       LIMIT 1`
    );

    if (!result.rows[0]) {
      return {
        globalSmbDomainUsername: '',
        globalSmbSecretRef: '',
        globalPrinterDomainUsername: '',
        globalPrinterSecretRef: '',
        workerPollIntervalMs: 5000,
        smtpEnabled: false,
        smtpHost: '',
        smtpPort: 25,
        smtpSecure: false,
        smtpUsername: '',
        smtpSecretRef: '',
        smtpFrom: '',
        smtpTo: []
      };
    }

    return mapSystemSettingsRow(result.rows[0]);
  }

  async updateSystemSettings(input: {
    globalSmbDomainUsername?: string;
    globalSmbSecretRef?: string;
    globalPrinterDomainUsername?: string;
    globalPrinterSecretRef?: string;
    workerPollIntervalMs?: number;
    smtpEnabled?: boolean;
    smtpHost?: string;
    smtpPort?: number;
    smtpSecure?: boolean;
    smtpUsername?: string;
    smtpSecretRef?: string;
    smtpFrom?: string;
    smtpTo?: string[];
  }): Promise<SystemSettingsRecord> {
    const result = await this.db.query<SystemSettingsRow>(
      `INSERT INTO system_settings(
         id,
         global_smb_domain_username,
         global_smb_secret_ref,
         global_printer_domain_username,
         global_printer_secret_ref,
         worker_poll_interval_ms,
         smtp_enabled,
         smtp_host,
         smtp_port,
         smtp_secure,
         smtp_username,
         smtp_secret_ref,
         smtp_from,
         smtp_to
       )
       VALUES (
         TRUE,
         COALESCE($1, ''),
         COALESCE($2, ''),
         COALESCE($3, ''),
         COALESCE($4, ''),
         COALESCE($5, 5000),
         COALESCE($6, FALSE),
         COALESCE($7, ''),
         COALESCE($8, 25),
         COALESCE($9, FALSE),
         COALESCE($10, ''),
         COALESCE($11, ''),
         COALESCE($12, ''),
         COALESCE($13, '[]'::jsonb)
       )
       ON CONFLICT (id)
       DO UPDATE SET
         global_smb_domain_username = COALESCE($1, system_settings.global_smb_domain_username),
         global_smb_secret_ref = COALESCE($2, system_settings.global_smb_secret_ref),
         global_printer_domain_username = COALESCE($3, system_settings.global_printer_domain_username),
         global_printer_secret_ref = COALESCE($4, system_settings.global_printer_secret_ref),
         worker_poll_interval_ms = COALESCE($5, system_settings.worker_poll_interval_ms),
         smtp_enabled = COALESCE($6, system_settings.smtp_enabled),
         smtp_host = COALESCE($7, system_settings.smtp_host),
         smtp_port = COALESCE($8, system_settings.smtp_port),
         smtp_secure = COALESCE($9, system_settings.smtp_secure),
         smtp_username = COALESCE($10, system_settings.smtp_username),
         smtp_secret_ref = COALESCE($11, system_settings.smtp_secret_ref),
         smtp_from = COALESCE($12, system_settings.smtp_from),
         smtp_to = COALESCE($13, system_settings.smtp_to),
         updated_at = NOW()
       RETURNING global_smb_domain_username,
                 global_smb_secret_ref,
                 global_printer_domain_username,
                 global_printer_secret_ref,
                 worker_poll_interval_ms,
                 smtp_enabled,
                 smtp_host,
                 smtp_port,
                 smtp_secure,
                 smtp_username,
                 smtp_secret_ref,
                 smtp_from,
                 smtp_to`,
      [
        input.globalSmbDomainUsername ?? null,
        input.globalSmbSecretRef ?? null,
        input.globalPrinterDomainUsername ?? null,
        input.globalPrinterSecretRef ?? null,
        input.workerPollIntervalMs ?? null,
        input.smtpEnabled ?? null,
        input.smtpHost ?? null,
        input.smtpPort ?? null,
        input.smtpSecure ?? null,
        input.smtpUsername ?? null,
        input.smtpSecretRef ?? null,
        input.smtpFrom ?? null,
        input.smtpTo ? JSON.stringify(input.smtpTo) : null
      ]
    );

    return mapSystemSettingsRow(result.rows[0]);
  }

  async saveRefreshToken(input: { userId: string; tokenHash: string; expiresAt: Date }): Promise<RefreshTokenRecord> {
    const result = await this.db.query<{
      id: string;
      user_id: string;
      token_hash: string;
      expires_at: Date;
      revoked_at: Date | null;
    }>(
      `INSERT INTO refresh_tokens(user_id, token_hash, expires_at)
       VALUES ($1, $2, $3)
       RETURNING id, user_id, token_hash, expires_at, revoked_at`,
      [input.userId, input.tokenHash, input.expiresAt]
    );

    const row = result.rows[0];
    return {
      id: row.id,
      userId: row.user_id,
      tokenHash: row.token_hash,
      expiresAt: row.expires_at,
      revokedAt: row.revoked_at
    };
  }

  async getRefreshTokenByHash(tokenHash: string): Promise<RefreshTokenRecord | null> {
    const result = await this.db.query<{
      id: string;
      user_id: string;
      token_hash: string;
      expires_at: Date;
      revoked_at: Date | null;
    }>(
      `SELECT id, user_id, token_hash, expires_at, revoked_at
       FROM refresh_tokens
       WHERE token_hash = $1`,
      [tokenHash]
    );

    if (!result.rows[0]) {
      return null;
    }

    const row = result.rows[0];
    return {
      id: row.id,
      userId: row.user_id,
      tokenHash: row.token_hash,
      expiresAt: row.expires_at,
      revokedAt: row.revoked_at
    };
  }

  async revokeRefreshToken(tokenHash: string): Promise<void> {
    await this.db.query('UPDATE refresh_tokens SET revoked_at = NOW() WHERE token_hash = $1', [tokenHash]);
  }

  async writeAuditEvent(event: AuditEvent): Promise<void> {
    await this.db.query(
      `INSERT INTO audit_log(actor_user_id, action, status, target_type, target_id, metadata)
       VALUES ($1, $2, $3, $4, $5, $6::jsonb)`,
      [
        event.actorUserId ?? null,
        event.action,
        event.status,
        event.targetType ?? null,
        event.targetId ?? null,
        JSON.stringify(event.metadata ?? {})
      ]
    );
  }

  async listAuditEvents(limit = 200): Promise<AuditLogRecord[]> {
    const result = await this.db.query<{
      id: string;
      actor_user_id: string | null;
      actor_username: string | null;
      action: string;
      status: 'SUCCESS' | 'FAILURE';
      target_type: string | null;
      target_id: string | null;
      metadata: Record<string, unknown> | null;
      created_at: Date;
    }>(
      `SELECT
         a.id::text AS id,
         a.actor_user_id,
         u.username AS actor_username,
         a.action,
         a.status,
         a.target_type,
         a.target_id,
         a.metadata,
         a.created_at
       FROM audit_log a
       LEFT JOIN users u ON u.id = a.actor_user_id
       ORDER BY a.created_at DESC, a.id DESC
       LIMIT $1`,
      [limit]
    );

    return result.rows.map((row) => ({
      id: row.id,
      actorUserId: row.actor_user_id ?? undefined,
      actorUsername: row.actor_username,
      action: row.action,
      status: row.status,
      targetType: row.target_type ?? undefined,
      targetId: row.target_id ?? undefined,
      metadata: row.metadata ?? {},
      createdAt: row.created_at
    }));
  }
}
