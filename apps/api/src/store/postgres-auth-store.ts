import type { Role } from '@printo/shared';
import type { Pool } from 'pg';
import type { AuditEvent, RefreshTokenRecord, UserRecord } from '../types.js';
import type { AuthStore } from './auth-store.js';

export class PostgresAuthStore implements AuthStore {
  constructor(private readonly db: Pool) {}

  async getUserByUsername(username: string): Promise<UserRecord | null> {
    const result = await this.db.query(
      `SELECT u.id, u.username, u.locale, u.theme, c.password_hash, c.algorithm,
              COALESCE(array_agg(r.role) FILTER (WHERE r.role IS NOT NULL), '{}') AS roles
       FROM users u
       LEFT JOIN user_credentials_local c ON c.user_id = u.id
       LEFT JOIN user_roles r ON r.user_id = u.id
       WHERE lower(u.username) = lower($1)
       GROUP BY u.id, c.password_hash, c.algorithm`,
      [username]
    );

    if (!result.rows[0] || !result.rows[0].password_hash) return null;
    const row = result.rows[0];
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

  async getUserById(userId: string): Promise<UserRecord | null> {
    const result = await this.db.query(
      `SELECT u.id, u.username, u.locale, u.theme, c.password_hash, c.algorithm,
              COALESCE(array_agg(r.role) FILTER (WHERE r.role IS NOT NULL), '{}') AS roles
       FROM users u
       LEFT JOIN user_credentials_local c ON c.user_id = u.id
       LEFT JOIN user_roles r ON r.user_id = u.id
       WHERE u.id = $1
       GROUP BY u.id, c.password_hash, c.algorithm`,
      [userId]
    );

    if (!result.rows[0]) return null;
    const row = result.rows[0];
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

  async listUsers(): Promise<UserRecord[]> {
    const result = await this.db.query(
      `SELECT u.id, u.username, u.locale, u.theme, c.password_hash, c.algorithm,
              COALESCE(array_agg(r.role) FILTER (WHERE r.role IS NOT NULL), '{}') AS roles
       FROM users u
       LEFT JOIN user_credentials_local c ON c.user_id = u.id
       LEFT JOIN user_roles r ON r.user_id = u.id
       GROUP BY u.id, c.password_hash, c.algorithm
       ORDER BY u.created_at ASC`
    );

    return result.rows.map((row) => ({
      id: row.id,
      username: row.username,
      locale: row.locale,
      theme: row.theme,
      roles: row.roles,
      passwordHash: row.password_hash,
      hashAlgorithm: row.algorithm
    }));
  }

  async createUser(input: { username: string; passwordHash: string; hashAlgorithm: string; roles: Role[] }): Promise<UserRecord> {
    const client = await this.db.connect();
    try {
      await client.query('BEGIN');
      const userRes = await client.query(
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

    const result = await this.db.query(
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
      const exists = await client.query('SELECT id FROM users WHERE id = $1', [input.userId]);
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

  async saveRefreshToken(input: { userId: string; tokenHash: string; expiresAt: Date }): Promise<RefreshTokenRecord> {
    const result = await this.db.query(
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
    const result = await this.db.query(
      `SELECT id, user_id, token_hash, expires_at, revoked_at
       FROM refresh_tokens WHERE token_hash = $1`,
      [tokenHash]
    );
    if (!result.rows[0]) return null;
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
