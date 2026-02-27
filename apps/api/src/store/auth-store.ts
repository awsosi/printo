import type { Role } from '@printo/shared';
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

export interface AuthStore {
  getUserByUsername(username: string): Promise<UserRecord | null>;
  getUserById(userId: string): Promise<UserRecord | null>;
  listUsers(): Promise<UserRecord[]>;
  createUser(input: {
    username: string;
    passwordHash: string;
    hashAlgorithm: string;
    roles: Role[];
    isRemoteEnabled?: boolean;
  }): Promise<UserRecord>;
  updateUserPreferences(input: { userId: string; locale?: string; theme?: string }): Promise<UserRecord | null>;
  setUserRoles(input: { userId: string; roles: Role[] }): Promise<UserRecord | null>;
  deleteUser(userId: string): Promise<boolean>;

  listSmbSources(): Promise<SmbSourceRecord[]>;
  createSmbSource(input: {
    ownerUserId?: string | null;
    path: string;
    domainUsername: string;
    secretRef: string;
    isActive: boolean;
  }): Promise<SmbSourceRecord>;
  updateSmbSource(input: {
    id: string;
    ownerUserId?: string | null;
    path?: string;
    domainUsername?: string;
    secretRef?: string;
    isActive?: boolean;
  }): Promise<SmbSourceRecord | null>;
  deleteSmbSource(id: string): Promise<boolean>;

  listPrinters(): Promise<PrinterRecord[]>;
  createPrinter(input: { name: string; type: PrinterType; targetUri: string; isActive: boolean }): Promise<PrinterRecord>;
  updatePrinter(input: {
    id: string;
    name?: string;
    type?: PrinterType;
    targetUri?: string;
    isActive?: boolean;
  }): Promise<PrinterRecord | null>;
  deletePrinter(id: string): Promise<boolean>;

  listFilenameMasks(): Promise<FilenameMaskRecord[]>;
  createFilenameMask(input: {
    ownerUserId?: string | null;
    pattern: string;
    isRegex: boolean;
    isActive: boolean;
  }): Promise<FilenameMaskRecord>;
  updateFilenameMask(input: {
    id: string;
    ownerUserId?: string | null;
    pattern?: string;
    isRegex?: boolean;
    isActive?: boolean;
  }): Promise<FilenameMaskRecord | null>;
  deleteFilenameMask(id: string): Promise<boolean>;

  listRoutingProfiles(): Promise<RoutingProfileRecord[]>;
  createRoutingProfile(input: {
    name: string;
    thermalLabelPatterns: string[];
    fallbackPrinterId?: string | null;
  }): Promise<RoutingProfileRecord>;
  updateRoutingProfile(input: {
    id: string;
    name?: string;
    thermalLabelPatterns?: string[];
    fallbackPrinterId?: string | null;
  }): Promise<RoutingProfileRecord | null>;
  deleteRoutingProfile(id: string): Promise<boolean>;

  getOcrGlobalConfig(): Promise<OcrGlobalConfigRecord>;
  updateOcrGlobalConfig(input: { provider?: string; config?: JsonObject }): Promise<OcrGlobalConfigRecord>;
  listOcrUserOverrides(): Promise<OcrUserOverrideRecord[]>;
  upsertOcrUserOverride(input: { userId: string; provider?: string | null; config: JsonObject }): Promise<OcrUserOverrideRecord>;
  deleteOcrUserOverride(userId: string): Promise<boolean>;

  saveRefreshToken(input: { userId: string; tokenHash: string; expiresAt: Date }): Promise<RefreshTokenRecord>;
  getRefreshTokenByHash(tokenHash: string): Promise<RefreshTokenRecord | null>;
  revokeRefreshToken(tokenHash: string): Promise<void>;
  writeAuditEvent(event: AuditEvent): Promise<void>;
}
