import type { Role } from '@printo/shared';
import type { AuditEvent, RefreshTokenRecord, UserRecord } from '../types.js';

export interface AuthStore {
  getUserByUsername(username: string): Promise<UserRecord | null>;
  getUserById(userId: string): Promise<UserRecord | null>;
  listUsers(): Promise<UserRecord[]>;
  createUser(input: { username: string; passwordHash: string; hashAlgorithm: string; roles: Role[] }): Promise<UserRecord>;
  updateUserPreferences(input: { userId: string; locale?: string; theme?: string }): Promise<UserRecord | null>;
  setUserRoles(input: { userId: string; roles: Role[] }): Promise<UserRecord | null>;
  deleteUser(userId: string): Promise<boolean>;
  saveRefreshToken(input: { userId: string; tokenHash: string; expiresAt: Date }): Promise<RefreshTokenRecord>;
  getRefreshTokenByHash(tokenHash: string): Promise<RefreshTokenRecord | null>;
  revokeRefreshToken(tokenHash: string): Promise<void>;
  writeAuditEvent(event: AuditEvent): Promise<void>;
}
