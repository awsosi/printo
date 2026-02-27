import type { Role } from '@printo/shared';
import type { Pool } from 'pg';
import type {
  AuditEvent,
  FilenameMaskRecord,
  JsonObject,
  OcrGlobalConfigRecord,
  OcrUserOverrideRecord,
  PrinterRecord,
  PrinterType,
  RefreshTokenRecord,
  RoutingProfileRecord,
  SmbSourceRecord,
  UserRecord
} from '../types.js';
import type { AuthStore } from './auth-store.js';

type UserRow = {
  id: string;
  username: string;
  locale: string;
  theme: string;
  password_hash: string;
  algorithm: string;
  roles: Role[];
};

type SmbSourceRow = {
  id: string;
  owner_user_id: string | null;
  path: string;
  domain_username: string;
  secret_ref: string;
  is_active: boolean;
};

type PrinterRow = {
  id: string;
  name: string;
  type: PrinterType;
  target_uri: string;
  is_active: boolean;
};

type FilenameMaskRow = {
  id: string;
  owner_user_id: string | null;
  pattern: string;
  is_regex: boolean;
  is_active: boolean;
};

type RoutingProfileRow = {
  id: string;
  name: string;
  thermal_label_patterns: unknown;
  fallback_printer_id: string | null;
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

function mapUserRow(row: UserRow): UserRecord {
  return {
    id: row.id,
    username: row.username,
    locale: row.locale,
    theme: row.theme,
    roles: row.roles,
    passwordHash: row.password_hash,
    hashAlgorithm: row.algorithm
  };
}

function mapSmbSourceRow(row: SmbSourceRow): SmbSourceRecord {
  return {
    id: row.id,
    ownerUserId: row.owner_user_id,
    path: row.path,
    domainUsername: row.domain_username,
    secretRef: row.secret_ref,
    isActive: row.is_active
  };
}

function mapPrinterRow(row: PrinterRow): PrinterRecord {
  return {
    id: row.id,
    name: row.name,
    type: row.type,
    targetUri: row.target_uri,
    isActive: row.is_active
  };
}

function mapFilenameMaskRow(row: FilenameMaskRow): FilenameMaskRecord {
  return {
    id: row.id,
    ownerUserId: row.owner_user_id,
    pattern: row.pattern,
    isRegex: row.is_regex,
    isActive: row.is_active
  };
}

function mapRoutingProfileRow(row: RoutingProfileRow): RoutingProfileRecord {
  return {
    id: row.id,
    name: row.name,
    thermalLabelPatterns: toStringArray(row.thermal_label_patterns),
    fallbackPrinterId: row.fallback_printer_id
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

export class PostgresAuthStore implements AuthStore {
  constructor(private readonly db: Pool) {}

  async getUserByUsername(username: string): Promise<UserRecord | null> {
    const result = await this.db.query<UserRow>(
      `SELECT u.id, u.username, u.locale, u.theme, c.password_hash, c.algorithm,
              COALESCE(array_agg(r.role) FILTER (WHERE r.role IS NOT NULL), '{}') AS roles
       FROM users u
       LEFT JOIN user_credentials_local c ON c.user_id = u.id
       LEFT JOIN user_roles r ON r.user_id = u.id
       WHERE lower(u.username) = lower($1)
       GROUP BY u.id, c.password_hash, c.algorithm`,
      [username]
    );

    if (!result.rows[0] || !result.rows[0].password_hash) {
      return null;
    }

    return mapUserRow(result.rows[0]);
  }

  async getUserById(userId: string): Promise<UserRecord | null> {
    const result = await this.db.query<UserRow>(
      `SELECT u.id, u.username, u.locale, u.theme, c.password_hash, c.algorithm,
              COALESCE(array_agg(r.role) FILTER (WHERE r.role IS NOT NULL), '{}') AS roles
       FROM users u
       LEFT JOIN user_credentials_local c ON c.user_id = u.id
       LEFT JOIN user_roles r ON r.user_id = u.id
       WHERE u.id = $1
       GROUP BY u.id, c.password_hash, c.algorithm`,
      [userId]
    );

    if (!result.rows[0]) {
      return null;
    }

    return mapUserRow(result.rows[0]);
  }

  async listUsers(): Promise<UserRecord[]> {
    const result = await this.db.query<UserRow>(
      `SELECT u.id, u.username, u.locale, u.theme, c.password_hash, c.algorithm,
              COALESCE(array_agg(r.role) FILTER (WHERE r.role IS NOT NULL), '{}') AS roles
       FROM users u
       LEFT JOIN user_credentials_local c ON c.user_id = u.id
       LEFT JOIN user_roles r ON r.user_id = u.id
       GROUP BY u.id, c.password_hash, c.algorithm
       ORDER BY u.created_at ASC`
    );

    return result.rows.map(mapUserRow);
  }

  async createUser(input: { username: string; passwordHash: string; hashAlgorithm: string; roles: Role[] }): Promise<UserRecord> {
    const client = await this.db.connect();
    try {
      await client.query('BEGIN');
      const userRes = await client.query<Pick<UserRow, 'id' | 'username' | 'locale' | 'theme'>>(
        `INSERT INTO users(username) VALUES ($1)
         RETURNING id, username, locale, theme`,
        [input.username]
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
        hashAlgorithm: input.hashAlgorithm
      };
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

  private async getSmbSourceById(id: string): Promise<SmbSourceRecord | null> {
    const result = await this.db.query<SmbSourceRow>(
      `SELECT id, owner_user_id, path, domain_username, secret_ref, is_active
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
      `SELECT id, owner_user_id, path, domain_username, secret_ref, is_active
       FROM smb_sources
       ORDER BY created_at ASC`
    );

    return result.rows.map(mapSmbSourceRow);
  }

  async createSmbSource(input: {
    ownerUserId?: string | null;
    path: string;
    domainUsername: string;
    secretRef: string;
    isActive: boolean;
  }): Promise<SmbSourceRecord> {
    const result = await this.db.query<SmbSourceRow>(
      `INSERT INTO smb_sources(owner_user_id, path, domain_username, secret_ref, is_active)
       VALUES ($1, $2, $3, $4, $5)
       RETURNING id, owner_user_id, path, domain_username, secret_ref, is_active`,
      [input.ownerUserId ?? null, input.path, input.domainUsername, input.secretRef, input.isActive]
    );

    return mapSmbSourceRow(result.rows[0]);
  }

  async updateSmbSource(input: {
    id: string;
    ownerUserId?: string | null;
    path?: string;
    domainUsername?: string;
    secretRef?: string;
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
      `SELECT id, name, type, target_uri, is_active
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
      `SELECT id, name, type, target_uri, is_active
       FROM printers
       ORDER BY created_at ASC`
    );

    return result.rows.map(mapPrinterRow);
  }

  async createPrinter(input: { name: string; type: PrinterType; targetUri: string; isActive: boolean }): Promise<PrinterRecord> {
    const result = await this.db.query<PrinterRow>(
      `INSERT INTO printers(name, type, target_uri, is_active)
       VALUES ($1, $2, $3, $4)
       RETURNING id, name, type, target_uri, is_active`,
      [input.name, input.type, input.targetUri, input.isActive]
    );

    return mapPrinterRow(result.rows[0]);
  }

  async updatePrinter(input: {
    id: string;
    name?: string;
    type?: PrinterType;
    targetUri?: string;
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

  private async getFilenameMaskById(id: string): Promise<FilenameMaskRecord | null> {
    const result = await this.db.query<FilenameMaskRow>(
      `SELECT id, owner_user_id, pattern, is_regex, is_active
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
      `SELECT id, owner_user_id, pattern, is_regex, is_active
       FROM filename_masks
       ORDER BY created_at ASC`
    );

    return result.rows.map(mapFilenameMaskRow);
  }

  async createFilenameMask(input: {
    ownerUserId?: string | null;
    pattern: string;
    isRegex: boolean;
    isActive: boolean;
  }): Promise<FilenameMaskRecord> {
    const result = await this.db.query<FilenameMaskRow>(
      `INSERT INTO filename_masks(owner_user_id, pattern, is_regex, is_active)
       VALUES ($1, $2, $3, $4)
       RETURNING id, owner_user_id, pattern, is_regex, is_active`,
      [input.ownerUserId ?? null, input.pattern, input.isRegex, input.isActive]
    );

    return mapFilenameMaskRow(result.rows[0]);
  }

  async updateFilenameMask(input: {
    id: string;
    ownerUserId?: string | null;
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
      `SELECT id, name, thermal_label_patterns, fallback_printer_id
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
      `SELECT id, name, thermal_label_patterns, fallback_printer_id
       FROM routing_profiles
       ORDER BY created_at ASC`
    );

    return result.rows.map(mapRoutingProfileRow);
  }

  async createRoutingProfile(input: {
    name: string;
    thermalLabelPatterns: string[];
    fallbackPrinterId?: string | null;
  }): Promise<RoutingProfileRecord> {
    const result = await this.db.query<RoutingProfileRow>(
      `INSERT INTO routing_profiles(name, thermal_label_patterns, fallback_printer_id)
       VALUES ($1, $2::jsonb, $3)
       RETURNING id, name, thermal_label_patterns, fallback_printer_id`,
      [input.name, JSON.stringify(input.thermalLabelPatterns), input.fallbackPrinterId ?? null]
    );

    return mapRoutingProfileRow(result.rows[0]);
  }

  async updateRoutingProfile(input: {
    id: string;
    name?: string;
    thermalLabelPatterns?: string[];
    fallbackPrinterId?: string | null;
  }): Promise<RoutingProfileRecord | null> {
    const setParts: string[] = [];
    const values: unknown[] = [input.id];
    let index = 2;

    if (input.name !== undefined) {
      setParts.push(`name = $${index}`);
      values.push(input.name);
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
}
