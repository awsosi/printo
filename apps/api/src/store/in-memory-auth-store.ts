import { randomUUID } from 'node:crypto';
import type { Role } from '@printo/shared';
import type {
  AdSyncConfigRecord,
  AuditEvent,
  AuditLogRecord,
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
  SmbSourceRecord,
  SystemSettingsRecord,
  UserPrinterAssignmentRecord,
  UserRecord,
  VisualMatchMode,
  VisualProfileRecord
} from '../types.js';
import type { AuthStore } from './auth-store.js';

export class InMemoryAuthStore implements AuthStore {
  private users: UserRecord[] = [];
  private refreshTokens: RefreshTokenRecord[] = [];

  private groups: GroupRecord[] = [];
  private groupMemberships: GroupMembershipRecord[] = [];
  private smbSources: SmbSourceRecord[] = [];
  private printers: PrinterRecord[] = [];
  private userPrinterAssignments: UserPrinterAssignmentRecord[] = [];
  private filenameMasks: FilenameMaskRecord[] = [];
  private routingProfiles: RoutingProfileRecord[] = [];
  private visualProfiles: VisualProfileRecord[] = [];
  private ocrGlobal: OcrGlobalConfigRecord = { provider: 'mock', config: {} };
  private ocrOverrides: OcrUserOverrideRecord[] = [];
  private adSyncConfig: AdSyncConfigRecord = {
    enabled: false,
    serverUrl: '',
    domain: '',
    baseDn: '',
    bindUsername: '',
    bindSecretRef: ''
  };
  private systemSettings: SystemSettingsRecord = {
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

  public auditEvents: AuditLogRecord[] = [];

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

  async updateUser(input: {
    userId: string;
    username?: string;
    isRemoteEnabled?: boolean;
    passwordHash?: string;
    hashAlgorithm?: string;
  }): Promise<UserRecord | null> {
    const user = this.users.find((candidate) => candidate.id === input.userId);
    if (!user) {
      return null;
    }

    if (input.username !== undefined) {
      user.username = input.username;
    }

    if (input.isRemoteEnabled !== undefined) {
      user.isRemoteEnabled = input.isRemoteEnabled;
    }

    if (input.passwordHash !== undefined) {
      user.passwordHash = input.passwordHash;
    }

    if (input.hashAlgorithm !== undefined) {
      user.hashAlgorithm = input.hashAlgorithm;
    }

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
    this.groupMemberships = this.groupMemberships.filter((membership) => membership.userId !== userId);
    this.userPrinterAssignments = this.userPrinterAssignments.filter((assignment) => assignment.userId !== userId);
    return this.users.length !== previousLength;
  }

  async listGroups(): Promise<GroupRecord[]> {
    return this.groups.map((group) => ({ ...group }));
  }

  async createGroup(input: { name: string; description?: string | null; isActive: boolean }): Promise<GroupRecord> {
    const created: GroupRecord = {
      id: randomUUID(),
      name: input.name,
      description: input.description ?? null,
      isActive: input.isActive
    };
    this.groups.push(created);
    return { ...created };
  }

  async updateGroup(input: { id: string; name?: string; description?: string | null; isActive?: boolean }): Promise<GroupRecord | null> {
    const record = this.groups.find((group) => group.id === input.id);
    if (!record) {
      return null;
    }

    if (input.name !== undefined) record.name = input.name;
    if (input.description !== undefined) record.description = input.description;
    if (input.isActive !== undefined) record.isActive = input.isActive;
    return { ...record };
  }

  async deleteGroup(id: string): Promise<boolean> {
    const initialCount = this.groups.length;
    this.groups = this.groups.filter((group) => group.id !== id);
    this.groupMemberships = this.groupMemberships.filter((membership) => membership.groupId !== id);
    return this.groups.length !== initialCount;
  }

  async listGroupMemberships(): Promise<GroupMembershipRecord[]> {
    return this.groupMemberships.map((membership) => ({ ...membership }));
  }

  async addGroupMembership(input: { groupId: string; userId: string }): Promise<GroupMembershipRecord> {
    const existing = this.groupMemberships.find(
      (membership) => membership.groupId === input.groupId && membership.userId === input.userId
    );
    if (existing) {
      return { ...existing };
    }

    const created: GroupMembershipRecord = {
      groupId: input.groupId,
      userId: input.userId
    };
    this.groupMemberships.push(created);
    return { ...created };
  }

  async deleteGroupMembership(input: { groupId: string; userId: string }): Promise<boolean> {
    const initialCount = this.groupMemberships.length;
    this.groupMemberships = this.groupMemberships.filter(
      (membership) => !(membership.groupId === input.groupId && membership.userId === input.userId)
    );
    return this.groupMemberships.length !== initialCount;
  }

  async listSmbSources(): Promise<SmbSourceRecord[]> {
    return [...this.smbSources];
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
    const record: SmbSourceRecord = {
      id: randomUUID(),
      ownerUserId: input.ownerUserId ?? null,
      ownerGroupId: input.ownerGroupId ?? null,
      path: input.path,
      domainUsername: input.domainUsername,
      secretRef: input.secretRef,
      printerDomainUsername: input.printerDomainUsername ?? '',
      printerSecretRef: input.printerSecretRef ?? '',
      routingProfileId: input.routingProfileId ?? null,
      a4PrinterId: input.a4PrinterId ?? null,
      thermalPrinterId: input.thermalPrinterId ?? null,
      includeFilenamePatterns: [...(input.includeFilenamePatterns ?? [])],
      excludeFilenamePatterns: [...(input.excludeFilenamePatterns ?? [])],
      isActive: input.isActive
    };
    this.smbSources.push(record);
    return record;
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
    const record = this.smbSources.find((candidate) => candidate.id === input.id);
    if (!record) {
      return null;
    }

    if (input.ownerUserId !== undefined) record.ownerUserId = input.ownerUserId;
    if (input.ownerGroupId !== undefined) record.ownerGroupId = input.ownerGroupId;
    if (input.path !== undefined) record.path = input.path;
    if (input.domainUsername !== undefined) record.domainUsername = input.domainUsername;
    if (input.secretRef !== undefined) record.secretRef = input.secretRef;
    if (input.printerDomainUsername !== undefined) record.printerDomainUsername = input.printerDomainUsername;
    if (input.printerSecretRef !== undefined) record.printerSecretRef = input.printerSecretRef;
    if (input.routingProfileId !== undefined) record.routingProfileId = input.routingProfileId;
    if (input.a4PrinterId !== undefined) record.a4PrinterId = input.a4PrinterId;
    if (input.thermalPrinterId !== undefined) record.thermalPrinterId = input.thermalPrinterId;
    if (input.includeFilenamePatterns !== undefined) record.includeFilenamePatterns = [...input.includeFilenamePatterns];
    if (input.excludeFilenamePatterns !== undefined) record.excludeFilenamePatterns = [...input.excludeFilenamePatterns];
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

  async createPrinter(input: {
    name: string;
    type: PrinterType;
    targetUri: string;
    domainUsername?: string;
    secretRef?: string;
    isActive: boolean;
  }): Promise<PrinterRecord> {
    const record: PrinterRecord = {
      id: randomUUID(),
      name: input.name,
      type: input.type,
      targetUri: input.targetUri,
      domainUsername: input.domainUsername ?? '',
      secretRef: input.secretRef ?? '',
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
    domainUsername?: string;
    secretRef?: string;
    isActive?: boolean;
  }): Promise<PrinterRecord | null> {
    const record = this.printers.find((candidate) => candidate.id === input.id);
    if (!record) {
      return null;
    }

    if (input.name !== undefined) record.name = input.name;
    if (input.type !== undefined) record.type = input.type;
    if (input.targetUri !== undefined) record.targetUri = input.targetUri;
    if (input.domainUsername !== undefined) record.domainUsername = input.domainUsername;
    if (input.secretRef !== undefined) record.secretRef = input.secretRef;
    if (input.isActive !== undefined) record.isActive = input.isActive;

    return record;
  }

  async deletePrinter(id: string): Promise<boolean> {
    const initialCount = this.printers.length;
    this.printers = this.printers.filter((record) => record.id !== id);
    this.userPrinterAssignments = this.userPrinterAssignments.map((assignment) => ({
      userId: assignment.userId,
      a4PrinterId: assignment.a4PrinterId === id ? null : assignment.a4PrinterId,
      thermalPrinterId: assignment.thermalPrinterId === id ? null : assignment.thermalPrinterId
    }));
    return this.printers.length !== initialCount;
  }

  async listUserPrinterAssignments(): Promise<UserPrinterAssignmentRecord[]> {
    return this.userPrinterAssignments.map((assignment) => ({ ...assignment }));
  }

  async getUserPrinterAssignment(userId: string): Promise<UserPrinterAssignmentRecord | null> {
    const assignment = this.userPrinterAssignments.find((candidate) => candidate.userId === userId);
    return assignment ? { ...assignment } : null;
  }

  async upsertUserPrinterAssignment(input: {
    userId: string;
    a4PrinterId?: string | null;
    thermalPrinterId?: string | null;
  }): Promise<UserPrinterAssignmentRecord> {
    const existing = this.userPrinterAssignments.find((candidate) => candidate.userId === input.userId);

    if (existing) {
      if (input.a4PrinterId !== undefined) {
        existing.a4PrinterId = input.a4PrinterId;
      }

      if (input.thermalPrinterId !== undefined) {
        existing.thermalPrinterId = input.thermalPrinterId;
      }

      return { ...existing };
    }

    const created: UserPrinterAssignmentRecord = {
      userId: input.userId,
      a4PrinterId: input.a4PrinterId ?? null,
      thermalPrinterId: input.thermalPrinterId ?? null
    };

    this.userPrinterAssignments.push(created);
    return { ...created };
  }

  async deleteUserPrinterAssignment(userId: string): Promise<boolean> {
    const initialCount = this.userPrinterAssignments.length;
    this.userPrinterAssignments = this.userPrinterAssignments.filter((assignment) => assignment.userId !== userId);
    return this.userPrinterAssignments.length !== initialCount;
  }

  async listFilenameMasks(): Promise<FilenameMaskRecord[]> {
    return [...this.filenameMasks];
  }

  async createFilenameMask(input: {
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    pattern: string;
    isRegex: boolean;
    isActive: boolean;
  }): Promise<FilenameMaskRecord> {
    const record: FilenameMaskRecord = {
      id: randomUUID(),
      ownerUserId: input.ownerUserId ?? null,
      ownerGroupId: input.ownerGroupId ?? null,
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
    ownerGroupId?: string | null;
    pattern?: string;
    isRegex?: boolean;
    isActive?: boolean;
  }): Promise<FilenameMaskRecord | null> {
    const record = this.filenameMasks.find((candidate) => candidate.id === input.id);
    if (!record) {
      return null;
    }

    if (input.ownerUserId !== undefined) record.ownerUserId = input.ownerUserId;
    if (input.ownerGroupId !== undefined) record.ownerGroupId = input.ownerGroupId;
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
    visualRules?: RoutingProfileRecord['visualRules'];
  }): Promise<RoutingProfileRecord> {
    const record: RoutingProfileRecord = {
      id: randomUUID(),
      name: input.name,
      ownerUserId: input.ownerUserId ?? null,
      ownerGroupId: input.ownerGroupId ?? null,
      printerDomainUsername: input.printerDomainUsername ?? '',
      printerSecretRef: input.printerSecretRef ?? '',
      defaultRouteType: input.defaultRouteType ?? 'A4',
      thermalLabelPatterns: [...input.thermalLabelPatterns],
      fallbackPrinterId: input.fallbackPrinterId ?? null,
      samplePdfName: input.samplePdfName ?? null,
      samplePdfBase64: input.samplePdfBase64 ?? null,
      snippetBase64: input.snippetBase64 ?? null,
      matchThreshold: input.matchThreshold ?? 0.88,
      visualRules: (input.visualRules ?? []).map((rule) => ({
        ...rule,
        expectedWords: [...rule.expectedWords],
        rect: { ...rule.rect }
      }))
    };

    this.routingProfiles.push(record);
    return record;
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
    visualRules?: RoutingProfileRecord['visualRules'];
  }): Promise<RoutingProfileRecord | null> {
    const record = this.routingProfiles.find((candidate) => candidate.id === input.id);
    if (!record) {
      return null;
    }

    if (input.name !== undefined) record.name = input.name;
    if (input.ownerUserId !== undefined) record.ownerUserId = input.ownerUserId;
    if (input.ownerGroupId !== undefined) record.ownerGroupId = input.ownerGroupId;
    if (input.printerDomainUsername !== undefined) record.printerDomainUsername = input.printerDomainUsername;
    if (input.printerSecretRef !== undefined) record.printerSecretRef = input.printerSecretRef;
    if (input.defaultRouteType !== undefined) record.defaultRouteType = input.defaultRouteType;
    if (input.thermalLabelPatterns !== undefined) record.thermalLabelPatterns = [...input.thermalLabelPatterns];
    if (input.fallbackPrinterId !== undefined) record.fallbackPrinterId = input.fallbackPrinterId;
    if (input.samplePdfName !== undefined) record.samplePdfName = input.samplePdfName;
    if (input.samplePdfBase64 !== undefined) record.samplePdfBase64 = input.samplePdfBase64;
    if (input.snippetBase64 !== undefined) record.snippetBase64 = input.snippetBase64;
    if (input.matchThreshold !== undefined) record.matchThreshold = input.matchThreshold;
    if (input.visualRules !== undefined) {
      record.visualRules = input.visualRules.map((rule) => ({
        ...rule,
        expectedWords: [...rule.expectedWords],
        rect: { ...rule.rect }
      }));
    }

    return record;
  }

  async deleteRoutingProfile(id: string): Promise<boolean> {
    const initialCount = this.routingProfiles.length;
    this.routingProfiles = this.routingProfiles.filter((record) => record.id !== id);
    return this.routingProfiles.length !== initialCount;
  }

  async listVisualProfiles(): Promise<VisualProfileRecord[]> {
    return this.visualProfiles.map((profile) => ({ ...profile, labels: [...profile.labels] }));
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
    const created: VisualProfileRecord = {
      id: randomUUID(),
      name: input.name,
      ownerUserId: input.ownerUserId ?? null,
      ownerGroupId: input.ownerGroupId ?? null,
      snippetBase64: input.snippetBase64,
      matchMode: input.matchMode,
      routeType: input.routeType ?? null,
      printerId: input.printerId ?? null,
      labels: input.labels ? [...input.labels] : [],
      isActive: input.isActive
    };
    this.visualProfiles.push(created);
    return { ...created, labels: [...created.labels] };
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
    const record = this.visualProfiles.find((profile) => profile.id === input.id);
    if (!record) {
      return null;
    }

    if (input.name !== undefined) record.name = input.name;
    if (input.ownerUserId !== undefined) record.ownerUserId = input.ownerUserId;
    if (input.ownerGroupId !== undefined) record.ownerGroupId = input.ownerGroupId;
    if (input.snippetBase64 !== undefined) record.snippetBase64 = input.snippetBase64;
    if (input.matchMode !== undefined) record.matchMode = input.matchMode;
    if (input.routeType !== undefined) record.routeType = input.routeType;
    if (input.printerId !== undefined) record.printerId = input.printerId;
    if (input.labels !== undefined) record.labels = [...input.labels];
    if (input.isActive !== undefined) record.isActive = input.isActive;
    return { ...record, labels: [...record.labels] };
  }

  async deleteVisualProfile(id: string): Promise<boolean> {
    const initialCount = this.visualProfiles.length;
    this.visualProfiles = this.visualProfiles.filter((profile) => profile.id !== id);
    return this.visualProfiles.length !== initialCount;
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

  async getAdSyncConfig(): Promise<AdSyncConfigRecord> {
    return { ...this.adSyncConfig };
  }

  async updateAdSyncConfig(input: {
    enabled?: boolean;
    serverUrl?: string;
    domain?: string;
    baseDn?: string;
    bindUsername?: string;
    bindSecretRef?: string;
  }): Promise<AdSyncConfigRecord> {
    if (input.enabled !== undefined) this.adSyncConfig.enabled = input.enabled;
    if (input.serverUrl !== undefined) this.adSyncConfig.serverUrl = input.serverUrl;
    if (input.domain !== undefined) this.adSyncConfig.domain = input.domain;
    if (input.baseDn !== undefined) this.adSyncConfig.baseDn = input.baseDn;
    if (input.bindUsername !== undefined) this.adSyncConfig.bindUsername = input.bindUsername;
    if (input.bindSecretRef !== undefined) this.adSyncConfig.bindSecretRef = input.bindSecretRef;
    return { ...this.adSyncConfig };
  }

  async getSystemSettings(): Promise<SystemSettingsRecord> {
    return { ...this.systemSettings, smtpTo: [...this.systemSettings.smtpTo] };
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
    if (input.globalSmbDomainUsername !== undefined) this.systemSettings.globalSmbDomainUsername = input.globalSmbDomainUsername;
    if (input.globalSmbSecretRef !== undefined) this.systemSettings.globalSmbSecretRef = input.globalSmbSecretRef;
    if (input.globalPrinterDomainUsername !== undefined) {
      this.systemSettings.globalPrinterDomainUsername = input.globalPrinterDomainUsername;
    }
    if (input.globalPrinterSecretRef !== undefined) this.systemSettings.globalPrinterSecretRef = input.globalPrinterSecretRef;
    if (input.workerPollIntervalMs !== undefined) this.systemSettings.workerPollIntervalMs = input.workerPollIntervalMs;
    if (input.smtpEnabled !== undefined) this.systemSettings.smtpEnabled = input.smtpEnabled;
    if (input.smtpHost !== undefined) this.systemSettings.smtpHost = input.smtpHost;
    if (input.smtpPort !== undefined) this.systemSettings.smtpPort = input.smtpPort;
    if (input.smtpSecure !== undefined) this.systemSettings.smtpSecure = input.smtpSecure;
    if (input.smtpUsername !== undefined) this.systemSettings.smtpUsername = input.smtpUsername;
    if (input.smtpSecretRef !== undefined) this.systemSettings.smtpSecretRef = input.smtpSecretRef;
    if (input.smtpFrom !== undefined) this.systemSettings.smtpFrom = input.smtpFrom;
    if (input.smtpTo !== undefined) this.systemSettings.smtpTo = [...input.smtpTo];
    return { ...this.systemSettings, smtpTo: [...this.systemSettings.smtpTo] };
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
    this.auditEvents.push({
      ...event,
      id: String(this.auditEvents.length + 1),
      actorUsername: event.actorUserId ? (this.users.find((user) => user.id === event.actorUserId)?.username ?? null) : null,
      createdAt: new Date()
    });
  }

  async listAuditEvents(limit = 200): Promise<AuditLogRecord[]> {
    return this.auditEvents.slice(-limit).reverse().map((event) => ({ ...event }));
  }
}
