import { randomUUID } from 'node:crypto';
import request from 'supertest';
import { expect, test } from 'vitest';
import { createApiApp } from '../../apps/api/src/app.js';
import { InMemoryAuthStore } from '../../apps/api/src/store/in-memory-auth-store.js';
import type {
  PrintJobPageRecord,
  PrintJobRecord,
  ProcessedFileRecord,
  WorkerConfigStore,
  WorkerFilenameMask,
  WorkerOcrGlobalConfig,
  WorkerOcrUserOverride,
  WorkerPrinter,
  WorkerRoutingProfile,
  WorkerSmbSource
} from '../../apps/worker/src/pipeline.js';
import { MockOcrProvider, RecordingPrinterDispatcher, WorkerPipeline } from '../../apps/worker/src/pipeline.js';
import { createWorkerApp } from '../../apps/worker/src/app.js';

class SharedConfigWorkerStore implements WorkerConfigStore {
  private readonly processedFiles: ProcessedFileRecord[] = [];
  private readonly printJobs: PrintJobRecord[] = [];
  private readonly printJobPages: PrintJobPageRecord[] = [];

  constructor(private readonly apiStore: InMemoryAuthStore) {}

  async listActiveSmbSources(): Promise<WorkerSmbSource[]> {
    const records = await this.apiStore.listSmbSources();
    return records.filter((record) => record.isActive);
  }

  async getSmbSource(sourceId: string): Promise<WorkerSmbSource | null> {
    const records = await this.apiStore.listSmbSources();
    return records.find((record) => record.id === sourceId) ?? null;
  }

  async listActiveFilenameMasks(ownerUserId: string | null, _ownerGroupId: string | null): Promise<WorkerFilenameMask[]> {
    const records = await this.apiStore.listFilenameMasks();
    return records.filter((record) => {
      if (!record.isActive) {
        return false;
      }

      if (!record.ownerUserId) {
        return true;
      }

      return record.ownerUserId === ownerUserId;
    });
  }

  async getRoutingProfile(_ownerUserId: string | null, _ownerGroupId: string | null): Promise<WorkerRoutingProfile | null> {
    const profiles = await this.apiStore.listRoutingProfiles();
    return profiles[0] ?? null;
  }

  async getRoutingProfileById(id: string): Promise<WorkerRoutingProfile | null> {
    const profiles = await this.apiStore.listRoutingProfiles();
    return profiles.find((profile) => profile.id === id) ?? null;
  }

  async getSystemSettings() {
    return {
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

  async listVisualProfiles(_ownerUserId: string | null, _ownerGroupId: string | null): Promise<[]> {
    return [];
  }

  async getActivePrinters(): Promise<WorkerPrinter[]> {
    const records = await this.apiStore.listPrinters();
    return records.filter((record) => record.isActive);
  }

  async getUserPrinterAssignment(userId: string) {
    return this.apiStore.getUserPrinterAssignment(userId);
  }

  async getOcrGlobalConfig(): Promise<WorkerOcrGlobalConfig> {
    return this.apiStore.getOcrGlobalConfig();
  }

  async getOcrUserOverride(userId: string): Promise<WorkerOcrUserOverride | null> {
    const records = await this.apiStore.listOcrUserOverrides();
    return records.find((record) => record.userId === userId) ?? null;
  }

  async isProcessedFile(input: { filePath: string; checksumSha256: string; fileMtime: Date | null }): Promise<boolean> {
    return this.processedFiles.some((record) => {
      const sameMtime =
        (record.fileMtime === null && input.fileMtime === null) ||
        (record.fileMtime !== null && input.fileMtime !== null && record.fileMtime.getTime() === input.fileMtime.getTime());

      return record.checksumSha256 === input.checksumSha256 || (record.filePath === input.filePath && sameMtime);
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

  async getPrintJob(jobId: string): Promise<PrintJobRecord | null> {
    const job = this.printJobs.find((record) => record.id === jobId);
    return job ? { ...job } : null;
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
    return this.printJobs.some(
      (job) =>
        job.isCancelled &&
        job.sourceId === input.sourceId &&
        job.filePath === input.filePath &&
        job.checksumSha256 === input.checksumSha256 &&
        ((job.fileMtime === null && input.fileMtime === null) ||
          (job.fileMtime !== null && input.fileMtime !== null && job.fileMtime.getTime() === input.fileMtime.getTime()))
    );
  }

  async listSuccessfulPageDispatches(input: {
    sourceId: string;
    filePath: string;
    checksumSha256: string;
    fileMtime: Date | null;
  }) {
    const jobIds = new Set(
      this.printJobs
        .filter(
          (job) =>
            job.sourceId === input.sourceId &&
            job.filePath === input.filePath &&
            job.checksumSha256 === input.checksumSha256 &&
            ((job.fileMtime === null && input.fileMtime === null) ||
              (job.fileMtime !== null && input.fileMtime !== null && job.fileMtime.getTime() === input.fileMtime.getTime()))
        )
        .map((job) => job.id)
    );

    return this.printJobPages
      .filter((page) => jobIds.has(page.printJobId) && page.status === 'SUCCESS')
      .map((page) => ({ pageNumber: page.pageNumber, routeType: page.routeType }));
  }

  async addPrintJobPage(input: PrintJobPageRecord): Promise<void> {
    this.printJobPages.push(input);
  }

  async finishPrintJob(input: { jobId: string; status: 'SUCCESS' | 'FAILURE'; errorMessage?: string }): Promise<void> {
    const record = this.printJobs.find((job) => job.id === input.jobId);
    if (!record) {
      return;
    }

    record.status = input.status;
    record.errorMessage = input.errorMessage ?? null;
  }

  async linkProcessedFileToJob(input: { jobId: string; sourceFileId: string }): Promise<void> {
    const record = this.printJobs.find((job) => job.id === input.jobId);
    if (!record) {
      return;
    }

    record.sourceFileId = input.sourceFileId;
  }
}

test('admin config to worker routing happy path', async () => {
  const apiStore = new InMemoryAuthStore();
  const workerStore = new SharedConfigWorkerStore(apiStore);
  const dispatcher = new RecordingPrinterDispatcher();

  const scanner = {
    scanSource: async (source: WorkerSmbSource) => [
      {
        sourceId: source.id,
        path: `${source.path}/invoice-e2e.pdf`,
        content: 'label page\nregular page',
        modifiedAt: new Date('2026-02-27T21:00:00.000Z')
      }
    ]
  };

  const pipeline = new WorkerPipeline(workerStore, scanner, new MockOcrProvider(), dispatcher);
  const { app: workerApp } = createWorkerApp({ pipeline });
  const api = createApiApp(apiStore);

  const adminUsername = `admin_e2e_${Date.now()}`;
  const adminPassword = 'AdminPass123!';

  const register = await request(api).post('/auth/register').send({
    username: adminUsername,
    password: adminPassword,
    roles: ['ADMIN']
  });
  expect(register.status).toBe(201);

  const login = await request(api).post('/auth/login').send({
    username: adminUsername,
    password: adminPassword
  });
  expect(login.status).toBe(200);

  const adminToken = login.body.accessToken as string;
  const authHeader = { Authorization: `Bearer ${adminToken}` };

  const createA4 = await request(api).post('/admin/config/printers').set(authHeader).send({
    name: 'A4-Office',
    type: 'A4',
    targetUri: 'ipp://a4.local/queue'
  });
  expect(createA4.status).toBe(201);
  const a4 = createA4.body;

  const createThermal = await request(api).post('/admin/config/printers').set(authHeader).send({
    name: 'GK420d',
    type: 'THERMAL',
    targetUri: 'socket://thermal.local:9100'
  });
  expect(createThermal.status).toBe(201);

  const createSource = await request(api).post('/admin/config/smb-sources').set(authHeader).send({
    path: '/virtual/smb/inbox',
    domainUsername: 'EXAMPLE\\serviceuser',
    secretRef: 'secret://smb/serviceuser'
  });
  expect(createSource.status).toBe(201);

  const createMask = await request(api).post('/admin/config/filename-masks').set(authHeader).send({
    pattern: 'invoice',
    isRegex: false
  });
  expect(createMask.status).toBe(201);

  const createRouting = await request(api).post('/admin/config/routing-profiles').set(authHeader).send({
    name: 'default',
    thermalLabelPatterns: ['label'],
    fallbackPrinterId: a4.id
  });
  expect(createRouting.status).toBe(201);

  const setOcr = await request(api).put('/admin/config/ocr/global').set(authHeader).send({
    provider: 'mock',
    config: {
      thermalKeyword: 'label'
    }
  });
  expect(setOcr.status).toBe(200);

  const runWorker = await request(workerApp).post('/pipeline/run-once');
  expect(runWorker.status).toBe(200);
  expect(runWorker.body.summary.filesProcessed).toBe(1);
  expect(runWorker.body.summary.pageDispatches).toBe(2);
  expect(runWorker.body.summary.filesSkippedDedup).toBe(0);

  const routes = dispatcher.calls.map((call) => call.routeType);
  expect(routes).toEqual(['THERMAL', 'A4']);

  const runWorkerAgain = await request(workerApp).post('/pipeline/run-once');
  expect(runWorkerAgain.status).toBe(200);
  expect(runWorkerAgain.body.summary.filesProcessed).toBe(0);
  expect(runWorkerAgain.body.summary.filesSkippedDedup).toBe(1);
});
