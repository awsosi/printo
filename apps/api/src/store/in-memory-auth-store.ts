import { randomUUID } from 'node:crypto';
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
import type { AuthStore } from './auth-store.js';

export class InMemoryAuthStore implements AuthStore {
  private users: UserRecord[] = [];
  private refreshTokens: RefreshTokenRecord[] = [];

  private smbSources: SmbSourceRecord[] = [];
  private printers: PrinterRecord[] = [];
  private filenameMasks: FilenameMaskRecord[] = [];
  private routingProfiles: RoutingProfileRecord[] = [];
  private ocrGlobal: OcrGlobalConfigRecord = { provider: 'mock', config: {} };
  private ocrOverrides: OcrUserOverrideRecord[] = [];

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

  async createUser(input: {
    username: string;
    passwordHash: string;
    hashAlgorithm: string;
    roles: Role[];
    isRemoteEnabled?: boolean;
  }): Promise<UserRecord> {
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
      theme: 'system',
      isRemoteEnabled: input.isRemoteEnabled ?? false
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
    this.ocrOverrides = this.ocrOverrides.filter((override) => override.userId !== userId);
    return this.users.length !== previousLength;
  }

  async listSmbSources(): Promise<SmbSourceRecord[]> {
    return [...this.smbSources];
  }

  async createSmbSource(input: {
    ownerUserId?: string | null;
    path: string;
    domainUsername: string;
    secretRef: string;
    isActive: boolean;
  }): Promise<SmbSourceRecord> {
    const record: SmbSourceRecord = {
      id: randomUUID(),
      ownerUserId: input.ownerUserId ?? null,
      path: input.path,
      domainUsername: input.domainUsername,
      secretRef: input.secretRef,
      isActive: input.isActive
    };
    this.smbSources.push(record);
    return record;
  }

  async updateSmbSource(input: {
    id: string;
    ownerUserId?: string | null;
    path?: string;
    domainUsername?: string;
    secretRef?: string;
    isActive?: boolean;
  }): Promise<SmbSourceRecord | null> {
    const record = this.smbSources.find((candidate) => candidate.id === input.id);
    if (!record) {
      return null;
    }

    if (input.ownerUserId !== undefined) record.ownerUserId = input.ownerUserId;
    if (input.path !== undefined) record.path = input.path;
    if (input.domainUsername !== undefined) record.domainUsername = input.domainUsername;
    if (input.secretRef !== undefined) record.secretRef = input.secretRef;
    if (input.isActive !== undefined) record.isActive = input.isActive;

    return record;
  }

  async deleteSmbSource(id: string): Promise<boolean> {
    const initialCount = this.smbSources.length;
    this.smbSources = this.smbSources.filter((record) => record.id !== id);
    return this.smbSources.length !== initialCount;
  }

  async listPrinters(): Promise<PrinterRecord[]> {
    return [...this.printers];
  }

  async createPrinter(input: { name: string; type: PrinterType; targetUri: string; isActive: boolean }): Promise<PrinterRecord> {
    const record: PrinterRecord = {
      id: randomUUID(),
      name: input.name,
      type: input.type,
      targetUri: input.targetUri,
      isActive: input.isActive
    };
    this.printers.push(record);
    return record;
  }

  async updatePrinter(input: {
    id: string;
    name?: string;
    type?: PrinterType;
    targetUri?: string;
    isActive?: boolean;
  }): Promise<PrinterRecord | null> {
    const record = this.printers.find((candidate) => candidate.id === input.id);
    if (!record) {
      return null;
    }

    if (input.name !== undefined) record.name = input.name;
    if (input.type !== undefined) record.type = input.type;
    if (input.targetUri !== undefined) record.targetUri = input.targetUri;
    if (input.isActive !== undefined) record.isActive = input.isActive;

    return record;
  }

  async deletePrinter(id: string): Promise<boolean> {
    const initialCount = this.printers.length;
    this.printers = this.printers.filter((record) => record.id !== id);
    return this.printers.length !== initialCount;
  }

  async listFilenameMasks(): Promise<FilenameMaskRecord[]> {
    return [...this.filenameMasks];
  }

  async createFilenameMask(input: {
    ownerUserId?: string | null;
    pattern: string;
    isRegex: boolean;
    isActive: boolean;
  }): Promise<FilenameMaskRecord> {
    const record: FilenameMaskRecord = {
      id: randomUUID(),
      ownerUserId: input.ownerUserId ?? null,
      pattern: input.pattern,
      isRegex: input.isRegex,
      isActive: input.isActive
    };

    this.filenameMasks.push(record);
    return record;
  }

  async updateFilenameMask(input: {
    id: string;
    ownerUserId?: string | null;
    pattern?: string;
    isRegex?: boolean;
    isActive?: boolean;
  }): Promise<FilenameMaskRecord | null> {
    const record = this.filenameMasks.find((candidate) => candidate.id === input.id);
    if (!record) {
      return null;
    }

    if (input.ownerUserId !== undefined) record.ownerUserId = input.ownerUserId;
    if (input.pattern !== undefined) record.pattern = input.pattern;
    if (input.isRegex !== undefined) record.isRegex = input.isRegex;
    if (input.isActive !== undefined) record.isActive = input.isActive;

    return record;
  }

  async deleteFilenameMask(id: string): Promise<boolean> {
    const initialCount = this.filenameMasks.length;
    this.filenameMasks = this.filenameMasks.filter((record) => record.id !== id);
    return this.filenameMasks.length !== initialCount;
  }

  async listRoutingProfiles(): Promise<RoutingProfileRecord[]> {
    return [...this.routingProfiles];
  }

  async createRoutingProfile(input: {
    name: string;
    thermalLabelPatterns: string[];
    fallbackPrinterId?: string | null;
  }): Promise<RoutingProfileRecord> {
    const record: RoutingProfileRecord = {
      id: randomUUID(),
      name: input.name,
      thermalLabelPatterns: [...input.thermalLabelPatterns],
      fallbackPrinterId: input.fallbackPrinterId ?? null
    };

    this.routingProfiles.push(record);
    return record;
  }

  async updateRoutingProfile(input: {
    id: string;
    name?: string;
    thermalLabelPatterns?: string[];
    fallbackPrinterId?: string | null;
  }): Promise<RoutingProfileRecord | null> {
    const record = this.routingProfiles.find((candidate) => candidate.id === input.id);
    if (!record) {
      return null;
    }

    if (input.name !== undefined) record.name = input.name;
    if (input.thermalLabelPatterns !== undefined) record.thermalLabelPatterns = [...input.thermalLabelPatterns];
    if (input.fallbackPrinterId !== undefined) record.fallbackPrinterId = input.fallbackPrinterId;

    return record;
  }

  async deleteRoutingProfile(id: string): Promise<boolean> {
    const initialCount = this.routingProfiles.length;
    this.routingProfiles = this.routingProfiles.filter((record) => record.id !== id);
    return this.routingProfiles.length !== initialCount;
  }

  async getOcrGlobalConfig(): Promise<OcrGlobalConfigRecord> {
    return {
      provider: this.ocrGlobal.provider,
      config: { ...this.ocrGlobal.config }
    };
  }

  async updateOcrGlobalConfig(input: { provider?: string; config?: JsonObject }): Promise<OcrGlobalConfigRecord> {
    if (input.provider !== undefined) {
      this.ocrGlobal.provider = input.provider;
    }

    if (input.config !== undefined) {
      this.ocrGlobal.config = { ...input.config };
    }

    return this.getOcrGlobalConfig();
  }

  async listOcrUserOverrides(): Promise<OcrUserOverrideRecord[]> {
    return this.ocrOverrides.map((override) => ({
      userId: override.userId,
      provider: override.provider,
      config: { ...override.config }
    }));
  }

  async upsertOcrUserOverride(input: {
    userId: string;
    provider?: string | null;
    config: JsonObject;
  }): Promise<OcrUserOverrideRecord> {
    const existing = this.ocrOverrides.find((override) => override.userId === input.userId);
    if (existing) {
      if (input.provider !== undefined) {
        existing.provider = input.provider;
      }
      existing.config = { ...input.config };
      return {
        userId: existing.userId,
        provider: existing.provider,
        config: { ...existing.config }
      };
    }

    const record: OcrUserOverrideRecord = {
      userId: input.userId,
      provider: input.provider ?? null,
      config: { ...input.config }
    };
    this.ocrOverrides.push(record);

    return {
      userId: record.userId,
      provider: record.provider,
      config: { ...record.config }
    };
  }

  async deleteOcrUserOverride(userId: string): Promise<boolean> {
    const initialCount = this.ocrOverrides.length;
    this.ocrOverrides = this.ocrOverrides.filter((override) => override.userId !== userId);
    return this.ocrOverrides.length !== initialCount;
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
    return this.refreshTokens.find((token) => token.tokenHash === tokenHash) ?? null;
  }

  async revokeRefreshToken(tokenHash: string): Promise<void> {
    const token = this.refreshTokens.find((candidate) => candidate.tokenHash === tokenHash);
    if (token) {
      token.revokedAt = new Date();
    }
  }

  async writeAuditEvent(event: AuditEvent): Promise<void> {
    this.auditEvents.push(event);
  }
}
