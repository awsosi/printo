import type { Role } from '@printo/shared';

export type JsonObject = Record<string, unknown>;

export type PrinterType = 'A4' | 'THERMAL';

export interface UserRecord {
  id: string;
  username: string;
  roles: Role[];
  passwordHash: string | null;
  hashAlgorithm: string | null;
  locale: string;
  theme: string;
  isRemoteEnabled: boolean;
}

export interface SmbSourceRecord {
  id: string;
  ownerUserId: string | null;
  path: string;
  domainUsername: string;
  secretRef: string;
  isActive: boolean;
}

export interface PrinterRecord {
  id: string;
  name: string;
  type: PrinterType;
  targetUri: string;
  isActive: boolean;
}

export interface UserPrinterAssignmentRecord {
  userId: string;
  a4PrinterId: string | null;
  thermalPrinterId: string | null;
}

export interface FilenameMaskRecord {
  id: string;
  ownerUserId: string | null;
  pattern: string;
  isRegex: boolean;
  isActive: boolean;
}

export interface RoutingProfileRecord {
  id: string;
  name: string;
  thermalLabelPatterns: string[];
  fallbackPrinterId: string | null;
}

export interface OcrGlobalConfigRecord {
  provider: string;
  config: JsonObject;
}

export interface OcrUserOverrideRecord {
  userId: string;
  provider: string | null;
  config: JsonObject;
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
