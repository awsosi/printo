import type { Role } from '@printo/shared';
import type {
  RoutingVisualRuleRecord,
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
  updateUser(input: {
    userId: string;
    username?: string;
    isRemoteEnabled?: boolean;
    passwordHash?: string;
    hashAlgorithm?: string;
  }): Promise<UserRecord | null>;
  updateUserPreferences(input: { userId: string; locale?: string; theme?: string }): Promise<UserRecord | null>;
  setUserRoles(input: { userId: string; roles: Role[] }): Promise<UserRecord | null>;
  deleteUser(userId: string): Promise<boolean>;

  listGroups(): Promise<GroupRecord[]>;
  createGroup(input: { name: string; description?: string | null; isActive: boolean }): Promise<GroupRecord>;
  updateGroup(input: { id: string; name?: string; description?: string | null; isActive?: boolean }): Promise<GroupRecord | null>;
  deleteGroup(id: string): Promise<boolean>;

  listGroupMemberships(): Promise<GroupMembershipRecord[]>;
  addGroupMembership(input: { groupId: string; userId: string }): Promise<GroupMembershipRecord>;
  deleteGroupMembership(input: { groupId: string; userId: string }): Promise<boolean>;

  listSmbSources(): Promise<SmbSourceRecord[]>;
  createSmbSource(input: {
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    path: string;
    domainUsername: string;
    secretRef: string;
    isActive: boolean;
  }): Promise<SmbSourceRecord>;
  updateSmbSource(input: {
    id: string;
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    path?: string;
    domainUsername?: string;
    secretRef?: string;
    isActive?: boolean;
  }): Promise<SmbSourceRecord | null>;
  deleteSmbSource(id: string): Promise<boolean>;

  listPrinters(): Promise<PrinterRecord[]>;
  createPrinter(input: {
    name: string;
    type: PrinterType;
    targetUri: string;
    domainUsername?: string;
    secretRef?: string;
    isActive: boolean;
  }): Promise<PrinterRecord>;
  updatePrinter(input: {
    id: string;
    name?: string;
    type?: PrinterType;
    targetUri?: string;
    domainUsername?: string;
    secretRef?: string;
    isActive?: boolean;
  }): Promise<PrinterRecord | null>;
  deletePrinter(id: string): Promise<boolean>;

  listUserPrinterAssignments(): Promise<UserPrinterAssignmentRecord[]>;
  getUserPrinterAssignment(userId: string): Promise<UserPrinterAssignmentRecord | null>;
  upsertUserPrinterAssignment(input: {
    userId: string;
    a4PrinterId?: string | null;
    thermalPrinterId?: string | null;
  }): Promise<UserPrinterAssignmentRecord>;
  deleteUserPrinterAssignment(userId: string): Promise<boolean>;

  listFilenameMasks(): Promise<FilenameMaskRecord[]>;
  createFilenameMask(input: {
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    pattern: string;
    isRegex: boolean;
    isActive: boolean;
  }): Promise<FilenameMaskRecord>;
  updateFilenameMask(input: {
    id: string;
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    pattern?: string;
    isRegex?: boolean;
    isActive?: boolean;
  }): Promise<FilenameMaskRecord | null>;
  deleteFilenameMask(id: string): Promise<boolean>;

  listRoutingProfiles(): Promise<RoutingProfileRecord[]>;
  createRoutingProfile(input: {
    name: string;
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    defaultRouteType?: PrinterType;
    thermalLabelPatterns: string[];
    fallbackPrinterId?: string | null;
    samplePdfName?: string | null;
    samplePdfBase64?: string | null;
    visualRules?: RoutingVisualRuleRecord[];
  }): Promise<RoutingProfileRecord>;
  updateRoutingProfile(input: {
    id: string;
    name?: string;
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    defaultRouteType?: PrinterType;
    thermalLabelPatterns?: string[];
    fallbackPrinterId?: string | null;
    samplePdfName?: string | null;
    samplePdfBase64?: string | null;
    visualRules?: RoutingVisualRuleRecord[];
  }): Promise<RoutingProfileRecord | null>;
  deleteRoutingProfile(id: string): Promise<boolean>;

  listVisualProfiles(): Promise<VisualProfileRecord[]>;
  createVisualProfile(input: {
    name: string;
    ownerUserId?: string | null;
    ownerGroupId?: string | null;
    snippetBase64: string;
    matchMode: VisualMatchMode;
    routeType?: PrinterType | null;
    printerId?: string | null;
    labels?: string[];
    isActive: boolean;
  }): Promise<VisualProfileRecord>;
  updateVisualProfile(input: {
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
  }): Promise<VisualProfileRecord | null>;
  deleteVisualProfile(id: string): Promise<boolean>;

  getOcrGlobalConfig(): Promise<OcrGlobalConfigRecord>;
  updateOcrGlobalConfig(input: { provider?: string; config?: JsonObject }): Promise<OcrGlobalConfigRecord>;
  listOcrUserOverrides(): Promise<OcrUserOverrideRecord[]>;
  upsertOcrUserOverride(input: { userId: string; provider?: string | null; config: JsonObject }): Promise<OcrUserOverrideRecord>;
  deleteOcrUserOverride(userId: string): Promise<boolean>;

  getAdSyncConfig(): Promise<AdSyncConfigRecord>;
  updateAdSyncConfig(input: {
    enabled?: boolean;
    serverUrl?: string;
    domain?: string;
    baseDn?: string;
    bindUsername?: string;
    bindSecretRef?: string;
  }): Promise<AdSyncConfigRecord>;

  getSystemSettings(): Promise<SystemSettingsRecord>;
  updateSystemSettings(input: {
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
  }): Promise<SystemSettingsRecord>;

  saveRefreshToken(input: { userId: string; tokenHash: string; expiresAt: Date }): Promise<RefreshTokenRecord>;
  getRefreshTokenByHash(tokenHash: string): Promise<RefreshTokenRecord | null>;
  revokeRefreshToken(tokenHash: string): Promise<void>;
  writeAuditEvent(event: AuditEvent): Promise<void>;
  listAuditEvents(limit?: number): Promise<AuditLogRecord[]>;
}
