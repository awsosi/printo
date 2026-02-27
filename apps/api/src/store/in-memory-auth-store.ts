import { randomUUID } from 'node:crypto';
import type { Role } from '@printo/shared';
import type { AuditEvent, RefreshTokenRecord, UserRecord } from '../types.js';
import type { AuthStore } from './auth-store.js';

export class InMemoryAuthStore implements AuthStore {
  private users: UserRecord[] = [];
  private refreshTokens: RefreshTokenRecord[] = [];
  public auditEvents: AuditEvent[] = [];

  async getUserByUsername(username: string): Promise<UserRecord | null> {
    return this.users.find((u) => u.username.toLowerCase() === username.toLowerCase()) ?? null;
  }

  async getUserById(userId: string): Promise<UserRecord | null> {
    return this.users.find((u) => u.id === userId) ?? null;
  }

  async listUsers(): Promise<UserRecord[]> {
    return [...this.users];
  }

  async createUser(input: { username: string; passwordHash: string; hashAlgorithm: string; roles: Role[] }): Promise<UserRecord> {
    const existing = await this.getUserByUsername(input.username);
    if (existing) {
      throw new Error('USER_EXISTS');
    }

    const user: UserRecord = {
      id: randomUUID(),
      username: input.username,
      roles: [...input.roles],
      passwordHash: input.passwordHash,
      hashAlgorithm: input.hashAlgorithm,
      locale: 'en-US',
      theme: 'system'
    };
    this.users.push(user);
    return user;
  }

  async updateUserPreferences(input: { userId: string; locale?: string; theme?: string }): Promise<UserRecord | null> {
    const user = this.users.find((candidate) => candidate.id === input.userId);
    if (!user) {
      return null;
    }

    if (input.locale) {
      user.locale = input.locale;
    }

    if (input.theme) {
      user.theme = input.theme;
    }

    return user;
  }

  async setUserRoles(input: { userId: string; roles: Role[] }): Promise<UserRecord | null> {
    const user = this.users.find((candidate) => candidate.id === input.userId);
    if (!user) {
      return null;
    }

    user.roles = [...input.roles];
    return user;
  }

  async deleteUser(userId: string): Promise<boolean> {
    const previousLength = this.users.length;
    this.users = this.users.filter((user) => user.id !== userId);
    this.refreshTokens = this.refreshTokens.filter((token) => token.userId !== userId);
    return this.users.length !== previousLength;
  }

  async saveRefreshToken(input: { userId: string; tokenHash: string; expiresAt: Date }): Promise<RefreshTokenRecord> {
    const token: RefreshTokenRecord = {
      id: randomUUID(),
      userId: input.userId,
      tokenHash: input.tokenHash,
      expiresAt: input.expiresAt,
      revokedAt: null
    };
    this.refreshTokens.push(token);
    return token;
  }

  async getRefreshTokenByHash(tokenHash: string): Promise<RefreshTokenRecord | null> {
    return this.refreshTokens.find((t) => t.tokenHash === tokenHash) ?? null;
  }

  async revokeRefreshToken(tokenHash: string): Promise<void> {
    const token = this.refreshTokens.find((t) => t.tokenHash === tokenHash);
    if (token) {
      token.revokedAt = new Date();
    }
  }

  async writeAuditEvent(event: AuditEvent): Promise<void> {
    this.auditEvents.push(event);
  }
}
