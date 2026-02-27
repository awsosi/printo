import { randomUUID } from 'node:crypto';
import { once } from 'node:events';
import type { AddressInfo } from 'node:net';
import type { Server } from 'node:http';
import { expect, test } from '@playwright/test';
import type { Express } from 'express';
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

  async listActiveFilenameMasks(ownerUserId: string | null): Promise<WorkerFilenameMask[]> {
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

  async getRoutingProfile(_ownerUserId: string | null): Promise<WorkerRoutingProfile | null> {
    const profiles = await this.apiStore.listRoutingProfiles();
    return profiles[0] ?? null;
  }

  async getActivePrinters(): Promise<WorkerPrinter[]> {
    const records = await this.apiStore.listPrinters();
    return records.filter((record) => record.isActive);
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
  }): Promise<PrintJobRecord> {
    const created: PrintJobRecord = {
      id: randomUUID(),
      sourceId: input.sourceId,
      sourceFileId: input.sourceFileId,
      filePath: input.filePath,
      status: 'PENDING',
      errorMessage: null
    };

    this.printJobs.push(created);
    return created;
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

async function listen(app: Express): Promise<{ server: Server; baseUrl: string }> {
  const server = app.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address() as AddressInfo;

  return {
    server,
    baseUrl: `http://127.0.0.1:${address.port}`
  };
}

test('admin config to worker routing happy path', async ({ request }) => {
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

  const api = await listen(createApiApp(apiStore));
  const worker = await listen(workerApp);

  try {
    const adminUsername = `admin_e2e_${Date.now()}`;
    const adminPassword = 'AdminPass123!';

    const register = await request.post(`${api.baseUrl}/auth/register`, {
      data: {
        username: adminUsername,
        password: adminPassword,
        roles: ['ADMIN']
      }
    });
    expect(register.status()).toBe(201);

    const login = await request.post(`${api.baseUrl}/auth/login`, {
      data: {
        username: adminUsername,
        password: adminPassword
      }
    });
    expect(login.status()).toBe(200);

    const loginBody = await login.json();
    const adminToken = loginBody.accessToken as string;

    const adminHeaders = {
      authorization: `Bearer ${adminToken}`
    };

    const createA4 = await request.post(`${api.baseUrl}/admin/config/printers`, {
      headers: adminHeaders,
      data: {
        name: 'A4-Office',
        type: 'A4',
        targetUri: 'ipp://a4.local/queue'
      }
    });
    expect(createA4.status()).toBe(201);
    const a4 = await createA4.json();

    const createThermal = await request.post(`${api.baseUrl}/admin/config/printers`, {
      headers: adminHeaders,
      data: {
        name: 'GK420d',
        type: 'THERMAL',
        targetUri: 'socket://thermal.local:9100'
      }
    });
    expect(createThermal.status()).toBe(201);

    const createSource = await request.post(`${api.baseUrl}/admin/config/smb-sources`, {
      headers: adminHeaders,
      data: {
        path: '/virtual/smb/inbox',
        domainUsername: 'EXAMPLE\\serviceuser',
        secretRef: 'secret://smb/serviceuser'
      }
    });
    expect(createSource.status()).toBe(201);

    const createMask = await request.post(`${api.baseUrl}/admin/config/filename-masks`, {
      headers: adminHeaders,
      data: {
        pattern: 'invoice',
        isRegex: false
      }
    });
    expect(createMask.status()).toBe(201);

    const createRouting = await request.post(`${api.baseUrl}/admin/config/routing-profiles`, {
      headers: adminHeaders,
      data: {
        name: 'default',
        thermalLabelPatterns: ['label'],
        fallbackPrinterId: a4.id
      }
    });
    expect(createRouting.status()).toBe(201);

    const setOcr = await request.put(`${api.baseUrl}/admin/config/ocr/global`, {
      headers: adminHeaders,
      data: {
        provider: 'mock',
        config: {
          thermalKeyword: 'label'
        }
      }
    });
    expect(setOcr.status()).toBe(200);

    const runWorker = await request.post(`${worker.baseUrl}/pipeline/run-once`);
    expect(runWorker.status()).toBe(200);

    const runBody = await runWorker.json();
    expect(runBody.summary.filesProcessed).toBe(1);
    expect(runBody.summary.pageDispatches).toBe(2);
    expect(runBody.summary.filesSkippedDedup).toBe(0);

    const routes = dispatcher.calls.map((call) => call.routeType);
    expect(routes).toEqual(['THERMAL', 'A4']);

    const runWorkerAgain = await request.post(`${worker.baseUrl}/pipeline/run-once`);
    expect(runWorkerAgain.status()).toBe(200);
    const secondBody = await runWorkerAgain.json();
    expect(secondBody.summary.filesProcessed).toBe(0);
    expect(secondBody.summary.filesSkippedDedup).toBe(1);
  } finally {
    await Promise.all([
      new Promise<void>((resolve) => api.server.close(() => resolve())),
      new Promise<void>((resolve) => worker.server.close(() => resolve()))
    ]);
  }
});
