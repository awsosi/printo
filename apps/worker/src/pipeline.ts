import { createHash, randomUUID } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extractPdfPages, type PdfTextItem } from './pdf.js';

export type RouteType = 'A4' | 'THERMAL';

export interface WorkerSmbSource {
  id: string;
  ownerUserId: string | null;
  ownerGroupId: string | null;
  path: string;
  domainUsername: string;
  secretRef: string;
  isActive: boolean;
}

export interface WorkerFilenameMask {
  id: string;
  ownerUserId: string | null;
  ownerGroupId: string | null;
  pattern: string;
  isRegex: boolean;
  isActive: boolean;
}

export interface WorkerPrinter {
  id: string;
  name: string;
  type: RouteType;
  targetUri: string;
  domainUsername: string;
  secretRef: string;
  isActive: boolean;
}

export interface WorkerSystemSettings {
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

export interface WorkerRoutingProfile {
  id: string;
  name: string;
  ownerUserId: string | null;
  ownerGroupId: string | null;
  defaultRouteType: RouteType;
  thermalLabelPatterns: string[];
  fallbackPrinterId: string | null;
  samplePdfName: string | null;
  samplePdfBase64: string | null;
  visualRules: WorkerRoutingVisualRule[];
}

export interface WorkerRoutingVisualRule {
  id: string;
  samplePageNumber: number;
  routeType: RouteType;
  matchMode: 'CONTAINS' | 'EXACT';
  expectedText: string;
  expectedWords: string[];
  rect: {
    x: number;
    y: number;
    width: number;
    height: number;
  };
}

export interface WorkerVisualProfile {
  id: string;
  name: string;
  ownerUserId: string | null;
  ownerGroupId: string | null;
  snippetBase64: string;
  matchMode: 'CONTAINS' | 'EXACT';
  routeType: RouteType | null;
  printerId: string | null;
  labels: string[];
  isActive: boolean;
}

export interface WorkerOcrGlobalConfig {
  provider: string;
  config: Record<string, unknown>;
}

export interface WorkerOcrUserOverride {
  userId: string;
  provider: string | null;
  config: Record<string, unknown>;
}

export interface WorkerUserPrinterAssignment {
  userId: string;
  a4PrinterId: string | null;
  thermalPrinterId: string | null;
}

export interface ScannedFile {
  sourceId: string;
  path: string;
  content: Buffer | string;
  modifiedAt: Date | null;
}

export interface OcrPageResult {
  pageNumber: number;
  labels: string[];
  text?: string;
  pageWidth?: number;
  pageHeight?: number;
  textItems?: PdfTextItem[];
  forcedRouteType?: RouteType;
  forcedPrinterId?: string | null;
}

export interface OcrDocumentResult {
  pages: OcrPageResult[];
}

export interface DispatchRequest {
  printer: WorkerPrinter;
  routeType: RouteType;
  file: ScannedFile;
  page: OcrPageResult;
}

export interface PrintJobPageRecord {
  printJobId: string;
  pageNumber: number;
  routeType: RouteType;
  printerId: string | null;
  status: 'SUCCESS' | 'FAILURE' | 'SKIPPED';
  errorMessage?: string;
}

export interface ProcessedFileRecord {
  id: string;
  sourceId: string;
  filePath: string;
  checksumSha256: string;
  fileMtime: Date | null;
}

export interface PrintJobRecord {
  id: string;
  sourceId: string;
  sourceFileId: string | null;
  filePath: string;
  checksumSha256: string;
  fileMtime: Date | null;
  isCancelled: boolean;
  status: 'PENDING' | 'SUCCESS' | 'FAILURE' | 'CANCELLED';
  errorMessage: string | null;
}

export interface PipelineRunSummary {
  sourcesScanned: number;
  filesDiscovered: number;
  filesMatched: number;
  filesProcessed: number;
  filesSkippedDedup: number;
  filesSkippedCancelled: number;
  jobsCreated: number;
  pageDispatches: number;
  pageDispatchesSkipped: number;
  failures: number;
}

export interface SuccessfulPageDispatchRecord {
  pageNumber: number;
  routeType: RouteType;
}

export interface WorkerConfigStore {
  listActiveSmbSources(): Promise<WorkerSmbSource[]>;
  listActiveFilenameMasks(ownerUserId: string | null, ownerGroupId: string | null): Promise<WorkerFilenameMask[]>;
  getRoutingProfile(ownerUserId: string | null, ownerGroupId: string | null): Promise<WorkerRoutingProfile | null>;
  getActivePrinters(): Promise<WorkerPrinter[]>;
  getSystemSettings(): Promise<WorkerSystemSettings>;
  listVisualProfiles(ownerUserId: string | null, ownerGroupId: string | null): Promise<WorkerVisualProfile[]>;
  getUserPrinterAssignment(userId: string): Promise<WorkerUserPrinterAssignment | null>;
  getOcrGlobalConfig(): Promise<WorkerOcrGlobalConfig>;
  getOcrUserOverride(userId: string): Promise<WorkerOcrUserOverride | null>;
  isProcessedFile(input: {
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<boolean>;
  markProcessedFile(input: {
    sourceId: string;
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<ProcessedFileRecord>;
  createPrintJob(input: {
    sourceId: string;
    sourceFileId: string | null;
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<PrintJobRecord>;
  listPrintJobs(limit?: number): Promise<PrintJobRecord[]>;
  listPrintJobPages(jobId: string): Promise<PrintJobPageRecord[]>;
  cancelPrintJob(jobId: string): Promise<PrintJobRecord | null>;
  retryPrintJob(jobId: string): Promise<PrintJobRecord | null>;
  isFileCancelled(input: {
    sourceId: string;
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<boolean>;
  listSuccessfulPageDispatches(input: {
    sourceId: string;
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<SuccessfulPageDispatchRecord[]>;
  addPrintJobPage(input: PrintJobPageRecord): Promise<void>;
  finishPrintJob(input: {
    jobId: string;
    status: 'SUCCESS' | 'FAILURE';
    errorMessage?: string;
  }): Promise<void>;
  linkProcessedFileToJob(input: {
    jobId: string;
    sourceFileId: string;
  }): Promise<void>;
}

export interface PipelineNotifier {
  notify(input: {
    kind: 'SOURCE_FAILURE' | 'JOB_FAILURE';
    source: WorkerSmbSource;
    job?: PrintJobRecord;
    errorMessage: string;
  }): Promise<void>;
}

export interface SmbScanner {
  scanSource(source: WorkerSmbSource): Promise<ScannedFile[]>;
}

export interface OcrProvider {
  analyze(input: {
    file: ScannedFile;
    provider: string;
    config: Record<string, unknown>;
  }): Promise<OcrDocumentResult>;
}

export interface PrinterDispatcher {
  dispatch(input: DispatchRequest): Promise<void>;
}

class NoopPipelineNotifier implements PipelineNotifier {
  async notify(): Promise<void> {}
}

function normalizeDate(value: Date | null): number | null {
  return value ? value.getTime() : null;
}

function createSummary(): PipelineRunSummary {
  return {
    sourcesScanned: 0,
    filesDiscovered: 0,
    filesMatched: 0,
    filesProcessed: 0,
    filesSkippedDedup: 0,
    filesSkippedCancelled: 0,
    jobsCreated: 0,
    pageDispatches: 0,
    pageDispatchesSkipped: 0,
    failures: 0
  };
}

function resolveSourceCredentials(source: WorkerSmbSource, settings: WorkerSystemSettings): WorkerSmbSource {
  return {
    ...source,
    domainUsername: source.domainUsername || settings.globalSmbDomainUsername,
    secretRef: source.secretRef || settings.globalSmbSecretRef
  };
}

function resolvePrinterCredentials(printer: WorkerPrinter, settings: WorkerSystemSettings): WorkerPrinter {
  return {
    ...printer,
    domainUsername: printer.domainUsername || settings.globalPrinterDomainUsername,
    secretRef: printer.secretRef || settings.globalPrinterSecretRef
  };
}

function toSha256(content: Buffer | string): string {
  const data = Buffer.isBuffer(content) ? content : Buffer.from(content);
  return createHash('sha256').update(data).digest('hex');
}

function globToRegex(pattern: string): RegExp {
  const escaped = pattern.replace(/[.+^${}()|[\]\\]/g, '\\$&');
  const withWildcards = escaped.replace(/\*/g, '.*').replace(/\?/g, '.');
  return new RegExp(`^${withWildcards}$`, 'i');
}

function matchesMask(filePath: string, mask: WorkerFilenameMask): boolean {
  if (mask.isRegex) {
    try {
      return new RegExp(mask.pattern, 'i').test(filePath);
    } catch {
      return false;
    }
  }

  if (mask.pattern.includes('*') || mask.pattern.includes('?')) {
    return globToRegex(mask.pattern).test(filePath);
  }

  return filePath.toLowerCase().includes(mask.pattern.toLowerCase());
}

function isMaskedIn(filePath: string, masks: WorkerFilenameMask[]): boolean {
  if (masks.length === 0) {
    return true;
  }

  return masks.some((mask) => matchesMask(filePath, mask));
}

function matchesThermalPattern(label: string, pattern: string): boolean {
  const trimmed = pattern.trim();
  if (!trimmed) {
    return false;
  }

  if (trimmed.startsWith('/') && trimmed.endsWith('/') && trimmed.length > 2) {
    try {
      return new RegExp(trimmed.slice(1, -1), 'i').test(label);
    } catch {
      return false;
    }
  }

  return label.toLowerCase().includes(trimmed.toLowerCase());
}

function normalizeText(value: string): string {
  return value.replace(/\s+/g, ' ').trim().toLowerCase();
}

function extractRuleText(page: OcrPageResult, rule: WorkerRoutingVisualRule): string {
  if (!page.textItems || !page.pageWidth || !page.pageHeight) {
    return normalizeText(page.text ?? '');
  }

  const left = rule.rect.x * page.pageWidth;
  const top = rule.rect.y * page.pageHeight;
  const right = left + rule.rect.width * page.pageWidth;
  const bottom = top + rule.rect.height * page.pageHeight;
  return normalizeText(
    page.textItems
      .filter((item) => {
        const itemRight = item.x + item.width;
        const itemBottom = item.y + item.height;
        return itemRight >= left && item.x <= right && itemBottom >= top && item.y <= bottom;
      })
      .map((item) => item.text)
      .join(' ')
  );
}

function matchesVisualRule(page: OcrPageResult, rule: WorkerRoutingVisualRule): boolean {
  const regionText = extractRuleText(page, rule);
  if (!regionText) {
    return false;
  }

  const expectedText = normalizeText(rule.expectedText);
  if (rule.matchMode === 'EXACT' && expectedText) {
    return regionText === expectedText;
  }

  if (expectedText && regionText.includes(expectedText)) {
    return true;
  }

  const expectedWords = rule.expectedWords.map((word) => normalizeText(word)).filter(Boolean);
  if (expectedWords.length === 0) {
    return false;
  }

  return expectedWords.every((word) => regionText.includes(word));
}

function resolveRouteType(page: OcrPageResult, routing: WorkerRoutingProfile | null): RouteType {
  if (!routing) {
    return 'A4';
  }

  for (const rule of routing.visualRules) {
    if (matchesVisualRule(page, rule)) {
      return rule.routeType;
    }
  }

  for (const label of page.labels) {
    if (routing.thermalLabelPatterns.some((pattern) => matchesThermalPattern(label, pattern))) {
      return 'THERMAL';
    }
  }

  return routing.defaultRouteType;
}

function selectPrinter(input: {
  routeType: RouteType;
  printers: WorkerPrinter[];
  fallbackPrinterId: string | null;
  userAssignment: WorkerUserPrinterAssignment | null;
  forcedPrinterId: string | null;
}): WorkerPrinter {
  if (input.forcedPrinterId) {
    const forced = input.printers.find((printer) => printer.id === input.forcedPrinterId && printer.isActive);
    if (forced) {
      return forced;
    }
  }

  const assignedPrinterId = input.routeType === 'A4' ? input.userAssignment?.a4PrinterId : input.userAssignment?.thermalPrinterId;
  if (assignedPrinterId) {
    const assigned = input.printers.find((printer) => printer.id === assignedPrinterId && printer.isActive);
    if (assigned) {
      return assigned;
    }
  }

  const byType = input.printers.find((printer) => printer.type === input.routeType);
  if (byType) {
    return byType;
  }

  if (input.fallbackPrinterId) {
    const fallback = input.printers.find((printer) => printer.id === input.fallbackPrinterId);
    if (fallback) {
      return fallback;
    }
  }

  throw new Error(`PRINTER_NOT_CONFIGURED:${input.routeType}`);
}

export class WorkerPipeline {
  constructor(
    private readonly store: WorkerConfigStore,
    private readonly scanner: SmbScanner,
    private readonly ocrProvider: OcrProvider,
    private readonly dispatcher: PrinterDispatcher,
    private readonly notifier: PipelineNotifier = new NoopPipelineNotifier()
  ) {}

  async runOnce(): Promise<PipelineRunSummary> {
    const summary = createSummary();
    const sources = await this.store.listActiveSmbSources();
    const systemSettings = await this.store.getSystemSettings();
    const printers = (await this.store.getActivePrinters()).map((printer) => resolvePrinterCredentials(printer, systemSettings));
    const globalOcrConfig = await this.store.getOcrGlobalConfig();

    for (const source of sources) {
      const effectiveSource = resolveSourceCredentials(source, systemSettings);
      summary.sourcesScanned += 1;

      try {
        const masks = await this.store.listActiveFilenameMasks(effectiveSource.ownerUserId, effectiveSource.ownerGroupId);
        const routing = await this.store.getRoutingProfile(effectiveSource.ownerUserId, effectiveSource.ownerGroupId);
        const visualProfiles = await this.store.listVisualProfiles(effectiveSource.ownerUserId, effectiveSource.ownerGroupId);
        const userPrinterAssignment = effectiveSource.ownerUserId
          ? await this.store.getUserPrinterAssignment(effectiveSource.ownerUserId)
          : null;
        const ocrOverride = effectiveSource.ownerUserId ? await this.store.getOcrUserOverride(effectiveSource.ownerUserId) : null;
        const ocrProviderName = ocrOverride?.provider ?? globalOcrConfig.provider;
        const ocrConfig = {
          ...globalOcrConfig.config,
          ...(ocrOverride?.config ?? {}),
          visualProfiles
        };

        const files = await this.scanner.scanSource(effectiveSource);
        summary.filesDiscovered += files.length;

        for (const file of files) {
          if (!isMaskedIn(file.path, masks)) {
            continue;
          }

          summary.filesMatched += 1;
          const checksumSha256 = toSha256(file.content);
          const isProcessed = await this.store.isProcessedFile({
            filePath: file.path,
            checksumSha256,
            fileMtime: file.modifiedAt
          });

          if (isProcessed) {
            summary.filesSkippedDedup += 1;
            continue;
          }

          const isCancelled = await this.store.isFileCancelled({
            sourceId: effectiveSource.id,
            filePath: file.path,
            checksumSha256,
            fileMtime: file.modifiedAt
          });

          if (isCancelled) {
            summary.filesSkippedCancelled += 1;
            continue;
          }

          const job = await this.store.createPrintJob({
            sourceId: effectiveSource.id,
            sourceFileId: null,
            filePath: file.path,
            checksumSha256,
            fileMtime: file.modifiedAt
          });
          summary.jobsCreated += 1;

          try {
            const successfulPages = await this.store.listSuccessfulPageDispatches({
              sourceId: effectiveSource.id,
              filePath: file.path,
              checksumSha256,
              fileMtime: file.modifiedAt
            });
            const successfulPageKeys = new Set(successfulPages.map((page) => `${page.pageNumber}:${page.routeType}`));
            const ocrResult = await this.ocrProvider.analyze({
              file,
              provider: ocrProviderName,
              config: ocrConfig
            });

            for (const page of ocrResult.pages) {
              const routeType = resolveRouteType(page, routing);
              const effectiveRouteType = page.forcedRouteType ?? routeType;
              const pageKey = `${page.pageNumber}:${effectiveRouteType}`;
              if (successfulPageKeys.has(pageKey)) {
                await this.store.addPrintJobPage({
                  printJobId: job.id,
                  pageNumber: page.pageNumber,
                  routeType: effectiveRouteType,
                  printerId: null,
                  status: 'SKIPPED',
                  errorMessage: 'ALREADY_DISPATCHED_SUCCESSFULLY'
                });
                summary.pageDispatchesSkipped += 1;
                continue;
              }

              const printer = selectPrinter({
                routeType: effectiveRouteType,
                printers,
                fallbackPrinterId: routing?.fallbackPrinterId ?? null,
                userAssignment: userPrinterAssignment,
                forcedPrinterId: page.forcedPrinterId ?? null
              });

              await this.dispatcher.dispatch({
                routeType: effectiveRouteType,
                printer,
                file,
                page
              });

              await this.store.addPrintJobPage({
                printJobId: job.id,
                pageNumber: page.pageNumber,
                routeType: effectiveRouteType,
                printerId: printer.id,
                status: 'SUCCESS'
              });
              summary.pageDispatches += 1;
            }

            const processedFile = await this.store.markProcessedFile({
              sourceId: effectiveSource.id,
              filePath: file.path,
              checksumSha256,
              fileMtime: file.modifiedAt
            });

            await this.store.linkProcessedFileToJob({
              jobId: job.id,
              sourceFileId: processedFile.id
            });

            await this.store.finishPrintJob({
              jobId: job.id,
              status: 'SUCCESS'
            });

            job.sourceFileId = processedFile.id;
            job.status = 'SUCCESS';
            summary.filesProcessed += 1;
          } catch (error) {
            const message = error instanceof Error ? error.message : 'UNKNOWN_PIPELINE_ERROR';
            await this.store.addPrintJobPage({
              printJobId: job.id,
              pageNumber: 0,
              routeType: 'A4',
              printerId: null,
              status: 'FAILURE',
              errorMessage: message
            });
            await this.store.finishPrintJob({
              jobId: job.id,
              status: 'FAILURE',
              errorMessage: message
            });
            job.status = 'FAILURE';
            job.errorMessage = message;
            summary.failures += 1;
            await this.notifier.notify({
              kind: 'JOB_FAILURE',
              source: effectiveSource,
              job,
              errorMessage: message
            });
          }
        }
      } catch (error) {
        summary.failures += 1;
        await this.notifier.notify({
          kind: 'SOURCE_FAILURE',
          source: effectiveSource,
          errorMessage: error instanceof Error ? error.message : 'SOURCE_SCAN_FAILED'
        });
      }
    }

    return summary;
  }
}

interface InMemoryWorkerStoreInput {
  sources?: WorkerSmbSource[];
  masks?: WorkerFilenameMask[];
  printers?: WorkerPrinter[];
  userPrinterAssignments?: WorkerUserPrinterAssignment[];
  routingProfiles?: WorkerRoutingProfile[];
  visualProfiles?: WorkerVisualProfile[];
  ocrGlobalConfig?: WorkerOcrGlobalConfig;
  ocrUserOverrides?: WorkerOcrUserOverride[];
  processedFiles?: ProcessedFileRecord[];
  systemSettings?: WorkerSystemSettings;
}

export class InMemoryWorkerStore implements WorkerConfigStore {
  public readonly sources: WorkerSmbSource[];
  public readonly masks: WorkerFilenameMask[];
  public readonly printers: WorkerPrinter[];
  public readonly userPrinterAssignments: WorkerUserPrinterAssignment[];
  public readonly routingProfiles: WorkerRoutingProfile[];
  public readonly visualProfiles: WorkerVisualProfile[];
  public ocrGlobalConfig: WorkerOcrGlobalConfig;
  public readonly ocrUserOverrides: WorkerOcrUserOverride[];
  public readonly processedFiles: ProcessedFileRecord[];
  public systemSettings: WorkerSystemSettings;
  public readonly printJobs: PrintJobRecord[] = [];
  public readonly printJobPages: PrintJobPageRecord[] = [];

  constructor(input: InMemoryWorkerStoreInput = {}) {
    this.sources = input.sources ?? [];
    this.masks = input.masks ?? [];
    this.printers = input.printers ?? [];
    this.userPrinterAssignments = input.userPrinterAssignments ?? [];
    this.routingProfiles = input.routingProfiles ?? [];
    this.visualProfiles = input.visualProfiles ?? [];
    this.ocrGlobalConfig = input.ocrGlobalConfig ?? { provider: 'mock', config: {} };
    this.ocrUserOverrides = input.ocrUserOverrides ?? [];
    this.processedFiles = input.processedFiles ?? [];
    this.systemSettings = input.systemSettings ?? {
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
  }

  async listActiveSmbSources(): Promise<WorkerSmbSource[]> {
    return this.sources.filter((source) => source.isActive);
  }

  async listActiveFilenameMasks(ownerUserId: string | null, ownerGroupId: string | null): Promise<WorkerFilenameMask[]> {
    return this.masks.filter((mask) => {
      if (!mask.isActive) {
        return false;
      }

      if (mask.ownerUserId === null && mask.ownerGroupId === null) {
        return true;
      }

      if (ownerUserId !== null && mask.ownerUserId === ownerUserId) {
        return true;
      }

      return ownerGroupId !== null && mask.ownerGroupId === ownerGroupId;
    });
  }

  async getRoutingProfile(ownerUserId: string | null, ownerGroupId: string | null): Promise<WorkerRoutingProfile | null> {
    if (ownerUserId) {
      const byUser = this.routingProfiles.find((profile) => profile.ownerUserId === ownerUserId);
      if (byUser) {
        return byUser;
      }
    }

    if (ownerGroupId) {
      const byGroup = this.routingProfiles.find((profile) => profile.ownerGroupId === ownerGroupId);
      if (byGroup) {
        return byGroup;
      }
    }

    return this.routingProfiles.find((profile) => profile.ownerUserId === null && profile.ownerGroupId === null) ?? null;
  }

  async listVisualProfiles(ownerUserId: string | null, ownerGroupId: string | null): Promise<WorkerVisualProfile[]> {
    return this.visualProfiles.filter((profile) => {
      if (!profile.isActive) {
        return false;
      }
      if (profile.ownerUserId === null && profile.ownerGroupId === null) {
        return true;
      }
      if (ownerUserId && profile.ownerUserId === ownerUserId) {
        return true;
      }
      return Boolean(ownerGroupId && profile.ownerGroupId === ownerGroupId);
    });
  }

  async getActivePrinters(): Promise<WorkerPrinter[]> {
    return this.printers.filter((printer) => printer.isActive);
  }

  async getSystemSettings(): Promise<WorkerSystemSettings> {
    return { ...this.systemSettings, smtpTo: [...this.systemSettings.smtpTo] };
  }

  async getUserPrinterAssignment(userId: string): Promise<WorkerUserPrinterAssignment | null> {
    return this.userPrinterAssignments.find((assignment) => assignment.userId === userId) ?? null;
  }

  async getOcrGlobalConfig(): Promise<WorkerOcrGlobalConfig> {
    return this.ocrGlobalConfig;
  }

  async getOcrUserOverride(userId: string): Promise<WorkerOcrUserOverride | null> {
    return this.ocrUserOverrides.find((override) => override.userId === userId) ?? null;
  }

  async isProcessedFile(input: {
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<boolean> {
    const targetMtime = normalizeDate(input.fileMtime);
    return this.processedFiles.some((record) => {
      if (record.checksumSha256 === input.checksumSha256) {
        return true;
      }

      return record.filePath === input.filePath && normalizeDate(record.fileMtime) === targetMtime;
    });
  }

  async markProcessedFile(input: {
    sourceId: string;
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<ProcessedFileRecord> {
    const created: ProcessedFileRecord = {
      id: randomUUID(),
      sourceId: input.sourceId,
      filePath: input.filePath,
      checksumSha256: input.checksumSha256,
      fileMtime: input.fileMtime
    };
    this.processedFiles.push(created);
    return created;
  }

  async createPrintJob(input: {
    sourceId: string;
    sourceFileId: string | null;
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<PrintJobRecord> {
    const created: PrintJobRecord = {
      id: randomUUID(),
      sourceId: input.sourceId,
      sourceFileId: input.sourceFileId,
      filePath: input.filePath,
      checksumSha256: input.checksumSha256,
      fileMtime: input.fileMtime,
      isCancelled: false,
      status: 'PENDING',
      errorMessage: null
    };
    this.printJobs.push(created);
    return created;
  }

  async listPrintJobs(limit = 100): Promise<PrintJobRecord[]> {
    return this.printJobs.slice(-limit).reverse().map((job) => ({ ...job }));
  }

  async listPrintJobPages(jobId: string): Promise<PrintJobPageRecord[]> {
    return this.printJobPages.filter((page) => page.printJobId === jobId).map((page) => ({ ...page }));
  }

  async cancelPrintJob(jobId: string): Promise<PrintJobRecord | null> {
    const job = this.printJobs.find((record) => record.id === jobId);
    if (!job) {
      return null;
    }
    job.isCancelled = true;
    job.status = 'CANCELLED';
    return { ...job };
  }

  async retryPrintJob(jobId: string): Promise<PrintJobRecord | null> {
    const job = this.printJobs.find((record) => record.id === jobId);
    if (!job) {
      return null;
    }
    job.isCancelled = false;
    if (job.status === 'CANCELLED') {
      job.status = 'FAILURE';
    }
    return { ...job };
  }

  async isFileCancelled(input: {
    sourceId: string;
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<boolean> {
    const targetMtime = normalizeDate(input.fileMtime);
    return this.printJobs.some(
      (job) =>
        job.isCancelled &&
        job.sourceId === input.sourceId &&
        job.filePath === input.filePath &&
        job.checksumSha256 === input.checksumSha256 &&
        normalizeDate(job.fileMtime) === targetMtime
    );
  }

  async listSuccessfulPageDispatches(input: {
    sourceId: string;
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }): Promise<SuccessfulPageDispatchRecord[]> {
    const targetMtime = normalizeDate(input.fileMtime);
    const matchingJobs = new Set(
      this.printJobs
        .filter(
          (job) =>
            job.sourceId === input.sourceId &&
            job.filePath === input.filePath &&
            job.checksumSha256 === input.checksumSha256 &&
            normalizeDate(job.fileMtime) === targetMtime
        )
        .map((job) => job.id)
    );

    return this.printJobPages
      .filter((page) => matchingJobs.has(page.printJobId) && page.status === 'SUCCESS')
      .map((page) => ({ pageNumber: page.pageNumber, routeType: page.routeType }));
  }

  async linkProcessedFileToJob(input: { jobId: string; sourceFileId: string }): Promise<void> {
    const job = this.printJobs.find((record) => record.id === input.jobId);
    if (!job) {
      return;
    }

    job.sourceFileId = input.sourceFileId;
  }

  async addPrintJobPage(input: PrintJobPageRecord): Promise<void> {
    this.printJobPages.push(input);
  }

  async finishPrintJob(input: {
    jobId: string;
    status: 'SUCCESS' | 'FAILURE';
    errorMessage?: string;
  }): Promise<void> {
    const job = this.printJobs.find((record) => record.id === input.jobId);
    if (!job) {
      return;
    }

    job.status = input.status;
    job.errorMessage = input.errorMessage ?? null;
  }
}

export class StaticSmbScanner implements SmbScanner {
  constructor(private readonly sourceFiles: Record<string, ScannedFile[]>) {}

  async scanSource(source: WorkerSmbSource): Promise<ScannedFile[]> {
    return this.sourceFiles[source.id] ?? [];
  }
}

export class MockOcrProvider implements OcrProvider {
  private readonly execFileAsync = promisify(execFile);

  async analyze(input: {
    file: ScannedFile;
    provider: string;
    config: Record<string, unknown>;
  }): Promise<OcrDocumentResult> {
    const visualProfiles = Array.isArray(input.config.visualProfiles) ? input.config.visualProfiles : [];
    const buffer = Buffer.isBuffer(input.file.content) ? input.file.content : Buffer.from(input.file.content);
    const isPdf = input.file.path.toLowerCase().endsWith('.pdf');

    const visualMatch = this.matchVisualProfile(buffer, visualProfiles);
    if (isPdf) {
      try {
        const pdfPages = await extractPdfPages(buffer);
        if (pdfPages.length > 0) {
          return {
            pages: pdfPages.map((page) => ({
              pageNumber: page.pageNumber,
              labels: page.text.toLowerCase().includes(String(input.config.thermalKeyword ?? 'label').toLowerCase())
                ? [String(input.config.thermalKeyword ?? 'label').toLowerCase()]
                : [],
              text: page.text,
              pageWidth: page.width,
              pageHeight: page.height,
              textItems: page.items,
              forcedRouteType: visualMatch?.forcedRouteType,
              forcedPrinterId: visualMatch?.forcedPrinterId ?? null
            }))
          };
        }
      } catch {
        // Fall through to text-based mock behavior when PDF extraction fails.
      }
    }

    if (input.provider === 'tesseract') {
      const tesseractResult = await this.runTesseract(buffer, input.file.path);
      return {
        pages: tesseractResult.pages.map((page) => ({
          ...page,
          labels: visualMatch ? [...page.labels, ...visualMatch.labels] : page.labels,
          forcedRouteType: visualMatch?.forcedRouteType,
          forcedPrinterId: visualMatch?.forcedPrinterId ?? null
        }))
      };
    }

    const rawContent = Buffer.isBuffer(input.file.content)
      ? input.file.content.toString('utf8')
      : input.file.content;

    const thermalKeyword = String(input.config.thermalKeyword ?? 'label').toLowerCase();

    const pages = rawContent
      .split('\n')
      .map((line, index) => ({
        pageNumber: index + 1,
        labels: line.toLowerCase().includes(thermalKeyword) ? [thermalKeyword] : [],
        text: line,
        forcedRouteType: visualMatch?.forcedRouteType,
        forcedPrinterId: visualMatch?.forcedPrinterId ?? null
      }))
      .filter((page) => page.text.trim().length > 0);

    if (visualMatch && pages.length > 0) {
      pages[0].labels = [...pages[0].labels, ...visualMatch.labels];
    }

    return {
      pages: pages.length > 0 ? pages : [{ pageNumber: 1, labels: [], text: '' }]
    };
  }

  private matchVisualProfile(buffer: Buffer, rawProfiles: unknown[]): {
    labels: string[];
    forcedRouteType?: RouteType;
    forcedPrinterId?: string | null;
  } | null {
    for (const entry of rawProfiles) {
      if (!entry || typeof entry !== 'object') {
        continue;
      }
      const profile = entry as Record<string, unknown>;
      if (profile.isActive === false) {
        continue;
      }
      const snippetBase64 = typeof profile.snippetBase64 === 'string' ? profile.snippetBase64 : '';
      if (!snippetBase64) {
        continue;
      }
      let snippet: Buffer;
      try {
        snippet = Buffer.from(snippetBase64, 'base64');
      } catch {
        continue;
      }
      if (snippet.length === 0) {
        continue;
      }

      const matchMode = profile.matchMode === 'EXACT' ? 'EXACT' : 'CONTAINS';
      const matches = matchMode === 'EXACT' ? buffer.equals(snippet) : buffer.indexOf(snippet) >= 0;
      if (!matches) {
        continue;
      }

      const labels = Array.isArray(profile.labels) ? profile.labels.filter((item): item is string => typeof item === 'string') : [];
      const routeType = profile.routeType === 'A4' || profile.routeType === 'THERMAL' ? profile.routeType : undefined;
      const forcedPrinterId = profile.printerId === null || typeof profile.printerId === 'string' ? profile.printerId : null;
      return {
        labels,
        forcedRouteType: routeType,
        forcedPrinterId
      };
    }

    return null;
  }

  private async runTesseract(buffer: Buffer, originalPath: string): Promise<OcrDocumentResult> {
    const dir = await mkdtemp(join(tmpdir(), 'printo-ocr-'));
    const ext = originalPath.toLowerCase().endsWith('.png')
      ? '.png'
      : originalPath.toLowerCase().endsWith('.jpg') || originalPath.toLowerCase().endsWith('.jpeg')
        ? '.jpg'
        : '.bin';
    const inputPath = join(dir, `input${ext}`);
    try {
      await writeFile(inputPath, buffer);
      const { stdout } = await this.execFileAsync('tesseract', [inputPath, 'stdout', '--psm', '6'], {
        timeout: 15000,
        maxBuffer: 10 * 1024 * 1024
      });
      const text = stdout.trim();
      return {
        pages: [
          {
            pageNumber: 1,
            labels: [],
            text
          }
        ]
      };
    } catch {
      // Fallback keeps pipeline operational when Tesseract is unavailable or input is unsupported.
      return {
        pages: [
          {
            pageNumber: 1,
            labels: [],
            text: ''
          }
        ]
      };
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  }
}

export class RecordingPrinterDispatcher implements PrinterDispatcher {
  public readonly calls: DispatchRequest[] = [];

  async dispatch(input: DispatchRequest): Promise<void> {
    this.calls.push(input);
  }
}
