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
  ownerGroupId: string | null;
  path: string;
  domainUsername: string;
  secretRef: string;
  printerDomainUsername: string;
  printerSecretRef: string;
  routingProfileId: string | null;
  a4PrinterId: string | null;
  thermalPrinterId: string | null;
  includeFilenamePatterns: string[];
  excludeFilenamePatterns: string[];
  isActive: boolean;
}

export interface PrinterRecord {
  id: string;
  name: string;
  type: PrinterType;
  targetUri: string;
  domainUsername: string;
  secretRef: string;
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
  ownerGroupId: string | null;
  pattern: string;
  isRegex: boolean;
  isActive: boolean;
}

export interface RoutingProfileRecord {
  id: string;
  name: string;
  ownerUserId: string | null;
  ownerGroupId: string | null;
  printerDomainUsername: string;
  printerSecretRef: string;
  defaultRouteType: PrinterType;
  thermalLabelPatterns: string[];
  fallbackPrinterId: string | null;
  samplePdfName: string | null;
  samplePdfBase64: string | null;
  snippetBase64: string | null;
  matchThreshold: number;
  visualRules: RoutingVisualRuleRecord[];
  classificationRoutes: ClassificationRouteRecord[];
}

export type PageClass = 'OUTGOING_LABEL_THERMAL' | 'RETURN_LABEL_A4' | 'DOCUMENT_A4';

export interface ClassificationRouteRecord {
  pageClass: PageClass;
  routeType: PrinterType;
  printerId: string | null;
  minConfidence: number;
}

export interface RoutingVisualRectRecord {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface RoutingVisualRuleRecord {
  id: string;
  samplePageNumber: number;
  routeType: PrinterType;
  matchMode: VisualMatchMode;
  expectedText: string;
  expectedWords: string[];
  rect: RoutingVisualRectRecord;
}

export interface GroupRecord {
  id: string;
  name: string;
  description: string | null;
  isActive: boolean;
}

export interface GroupMembershipRecord {
  groupId: string;
  userId: string;
}

export type VisualMatchMode = 'CONTAINS' | 'EXACT';

export interface VisualProfileRecord {
  id: string;
  name: string;
  ownerUserId: string | null;
  ownerGroupId: string | null;
  snippetBase64: string;
  matchMode: VisualMatchMode;
  routeType: PrinterType | null;
  printerId: string | null;
  labels: string[];
  isActive: boolean;
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

export interface AdSyncConfigRecord {
  enabled: boolean;
  serverUrl: string;
  domain: string;
  baseDn: string;
  bindUsername: string;
  bindSecretRef: string;
}

export interface SystemSettingsRecord {
  globalSmbDomainUsername: string;
  globalSmbSecretRef: string;
  globalPrinterDomainUsername: string;
  globalPrinterSecretRef: string;
  workerPollIntervalMs: number;
  smtpEnabled: boolean;
  smtpHost: string;
  smtpPort: number;
  smtpSecure: boolean;
  smtpUsername: string;
  smtpSecretRef: string;
  smtpFrom: string;
  smtpTo: string[];
}

export interface AdDiscoveredUser {
  id: string;
  username: string;
  displayName: string;
}

export interface AdDiscoveredGroup {
  id: string;
  name: string;
  memberUsernames: string[];
}

export interface AdDiscoveredSmbShare {
  id: string;
  path: string;
  domainUsername?: string;
}

export interface AdDiscoveredPrinter {
  id: string;
  name: string;
  targetUri?: string;
  type?: PrinterType;
}

export interface AdDiscoverySnapshot {
  users: AdDiscoveredUser[];
  groups: AdDiscoveredGroup[];
  smbShares: AdDiscoveredSmbShare[];
  printers: AdDiscoveredPrinter[];
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

export interface AuditLogRecord extends AuditEvent {
  id: string;
  actorUsername: string | null;
  createdAt: Date;
}
