import type { Role } from '@printo/shared';

export interface UserRecord {
  id: string;
  username: string;
  roles: Role[];
  passwordHash: string;
  hashAlgorithm: string;
  locale: string;
  theme: string;
}

export interface RefreshTokenRecord {
  id: string;
  userId: string;
  tokenHash: string;
  expiresAt: Date;
  revokedAt: Date | null;
}

export interface AuditEvent {
  actorUserId?: string;
  action: string;
  status: 'SUCCESS' | 'FAILURE';
  targetType?: string;
  targetId?: string;
  metadata?: Record<string, unknown>;
}
