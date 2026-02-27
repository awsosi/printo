import { createHash, randomUUID } from 'node:crypto';

export type RouteType = 'A4' | 'THERMAL';

export interface WorkerSmbSource {
  id: string;
  ownerUserId: string | null;
  path: string;
  domainUsername: string;
  secretRef: string;
  isActive: boolean;
}

export interface WorkerFilenameMask {
  id: string;
  ownerUserId: string | null;
  pattern: string;
  isRegex: boolean;
  isActive: boolean;
}

export interface WorkerPrinter {
  id: string;
  name: string;
  type: RouteType;
  targetUri: string;
  isActive: boolean;
}

export interface WorkerRoutingProfile {
  id: string;
  name: string;
  thermalLabelPatterns: string[];
  fallbackPrinterId: string | null;
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
  status: 'SUCCESS' | 'FAILURE';
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
  status: 'SUCCESS' | 'FAILURE';
  errorMessage: string | null;
}

export interface PipelineRunSummary {
  sourcesScanned: number;
  filesDiscovered: number;
  filesMatched: number;
  filesProcessed: number;
  filesSkippedDedup: number;
  jobsCreated: number;
  pageDispatches: number;
  failures: number;
}

export interface WorkerConfigStore {
  listActiveSmbSources(): Promise<WorkerSmbSource[]>;
  listActiveFilenameMasks(ownerUserId: string | null): Promise<WorkerFilenameMask[]>;
  getRoutingProfile(ownerUserId: string | null): Promise<WorkerRoutingProfile | null>;
  getActivePrinters(): Promise<WorkerPrinter[]>;
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
  }): Promise<PrintJobRecord>;
  addPrintJobPage(input: PrintJobPageRecord): Promise<void>;
  finishPrintJob(input: {
    jobId: string;
    status: 'SUCCESS' | 'FAILURE';
    errorMessage?: string;
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
    jobsCreated: 0,
    pageDispatches: 0,
    failures: 0
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

function resolveRouteType(page: OcrPageResult, routing: WorkerRoutingProfile | null): RouteType {
  if (!routing) {
    return 'A4';
  }

  for (const label of page.labels) {
    if (routing.thermalLabelPatterns.some((pattern) => matchesThermalPattern(label, pattern))) {
      return 'THERMAL';
    }
  }

  return 'A4';
}

function selectPrinter(input: {
  routeType: RouteType;
  printers: WorkerPrinter[];
  fallbackPrinterId: string | null;
}): WorkerPrinter {
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
    private readonly dispatcher: PrinterDispatcher
  ) {}

  async runOnce(): Promise<PipelineRunSummary> {
    const summary = createSummary();
    const sources = await this.store.listActiveSmbSources();
    const printers = await this.store.getActivePrinters();
    const globalOcrConfig = await this.store.getOcrGlobalConfig();

    for (const source of sources) {
      summary.sourcesScanned += 1;

      try {
        const masks = await this.store.listActiveFilenameMasks(source.ownerUserId);
        const routing = await this.store.getRoutingProfile(source.ownerUserId);
        const ocrOverride = source.ownerUserId ? await this.store.getOcrUserOverride(source.ownerUserId) : null;
        const ocrProviderName = ocrOverride?.provider ?? globalOcrConfig.provider;
        const ocrConfig = {
          ...globalOcrConfig.config,
          ...(ocrOverride?.config ?? {})
        };

        const files = await this.scanner.scanSource(source);
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

          const job = await this.store.createPrintJob({
            sourceId: source.id,
            sourceFileId: null,
            filePath: file.path
          });
          summary.jobsCreated += 1;

          try {
            const ocrResult = await this.ocrProvider.analyze({
              file,
              provider: ocrProviderName,
              config: ocrConfig
            });

            for (const page of ocrResult.pages) {
              const routeType = resolveRouteType(page, routing);
              const printer = selectPrinter({
                routeType,
                printers,
                fallbackPrinterId: routing?.fallbackPrinterId ?? null
              });

              await this.dispatcher.dispatch({
                routeType,
                printer,
                file,
                page
              });

              await this.store.addPrintJobPage({
                printJobId: job.id,
                pageNumber: page.pageNumber,
                routeType,
                printerId: printer.id,
                status: 'SUCCESS'
              });
              summary.pageDispatches += 1;
            }

            const processedFile = await this.store.markProcessedFile({
              sourceId: source.id,
              filePath: file.path,
              checksumSha256,
              fileMtime: file.modifiedAt
            });

            await this.store.finishPrintJob({
              jobId: job.id,
              status: 'SUCCESS'
            });

            job.sourceFileId = processedFile.id;
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
            summary.failures += 1;
          }
        }
      } catch {
        summary.failures += 1;
      }
    }

    return summary;
  }
}

interface InMemoryWorkerStoreInput {
  sources?: WorkerSmbSource[];
  masks?: WorkerFilenameMask[];
  printers?: WorkerPrinter[];
  routingProfiles?: WorkerRoutingProfile[];
  ocrGlobalConfig?: WorkerOcrGlobalConfig;
  ocrUserOverrides?: WorkerOcrUserOverride[];
  processedFiles?: ProcessedFileRecord[];
}

export class InMemoryWorkerStore implements WorkerConfigStore {
  public readonly sources: WorkerSmbSource[];
  public readonly masks: WorkerFilenameMask[];
  public readonly printers: WorkerPrinter[];
  public readonly routingProfiles: WorkerRoutingProfile[];
  public ocrGlobalConfig: WorkerOcrGlobalConfig;
  public readonly ocrUserOverrides: WorkerOcrUserOverride[];
  public readonly processedFiles: ProcessedFileRecord[];
  public readonly printJobs: PrintJobRecord[] = [];
  public readonly printJobPages: PrintJobPageRecord[] = [];

  constructor(input: InMemoryWorkerStoreInput = {}) {
    this.sources = input.sources ?? [];
    this.masks = input.masks ?? [];
    this.printers = input.printers ?? [];
    this.routingProfiles = input.routingProfiles ?? [];
    this.ocrGlobalConfig = input.ocrGlobalConfig ?? { provider: 'mock', config: {} };
    this.ocrUserOverrides = input.ocrUserOverrides ?? [];
    this.processedFiles = input.processedFiles ?? [];
  }

  async listActiveSmbSources(): Promise<WorkerSmbSource[]> {
    return this.sources.filter((source) => source.isActive);
  }

  async listActiveFilenameMasks(ownerUserId: string | null): Promise<WorkerFilenameMask[]> {
    return this.masks.filter((mask) => {
      if (!mask.isActive) {
        return false;
      }

      if (mask.ownerUserId === null) {
        return true;
      }

      return ownerUserId !== null && mask.ownerUserId === ownerUserId;
    });
  }

  async getRoutingProfile(_ownerUserId: string | null): Promise<WorkerRoutingProfile | null> {
    return this.routingProfiles[0] ?? null;
  }

  async getActivePrinters(): Promise<WorkerPrinter[]> {
    return this.printers.filter((printer) => printer.isActive);
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
  }): Promise<PrintJobRecord> {
    const created: PrintJobRecord = {
      id: randomUUID(),
      sourceId: input.sourceId,
      sourceFileId: input.sourceFileId,
      filePath: input.filePath,
      status: 'SUCCESS',
      errorMessage: null
    };
    this.printJobs.push(created);
    return created;
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
  async analyze(input: {
    file: ScannedFile;
    provider: string;
    config: Record<string, unknown>;
  }): Promise<OcrDocumentResult> {
    const rawContent = Buffer.isBuffer(input.file.content)
      ? input.file.content.toString('utf8')
      : input.file.content;

    const thermalKeyword = String(input.config.thermalKeyword ?? 'label').toLowerCase();

    const pages = rawContent
      .split('\n')
      .map((line, index) => ({
        pageNumber: index + 1,
        labels: line.toLowerCase().includes(thermalKeyword) ? [thermalKeyword] : [],
        text: line
      }))
      .filter((page) => page.text.trim().length > 0);

    return {
      pages: pages.length > 0 ? pages : [{ pageNumber: 1, labels: [], text: '' }]
    };
  }
}

export class RecordingPrinterDispatcher implements PrinterDispatcher {
  public readonly calls: DispatchRequest[] = [];

  async dispatch(input: DispatchRequest): Promise<void> {
    this.calls.push(input);
  }
}
