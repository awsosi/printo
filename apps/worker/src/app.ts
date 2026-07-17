import express from 'express';
import { createDefaultPageClassifier } from './classify/index.js';
import { pool } from './db/pool.js';
import { ProviderPrinterDispatcher } from './dispatch/provider-printer-dispatcher.js';
import {
  InMemoryWorkerStore,
  MockOcrProvider,
  StaticSmbScanner,
  WorkerPipeline,
  type PipelineRunSummary,
  type WorkerConfigStore,
  type SmbScanner,
  type PrinterDispatcher,
  type OcrProvider
} from './pipeline.js';
import { WorkerRunner } from './runner.js';
import { AutoSmbScanner } from './scanner/auto-smb-scanner.js';
import { FilesystemSmbScanner } from './scanner/filesystem-smb-scanner.js';
import { SmbClientScanner } from './scanner/smb-client-scanner.js';
import { SmtpNotificationService, type NotificationAttemptRecord } from './smtp.js';
import { PostgresWorkerStore } from './store/postgres-worker-store.js';

export interface WorkerAppOptions {
  pipeline?: WorkerPipeline;
  runner?: WorkerRunner;
  store?: WorkerConfigStore;
  notifications?: SmtpNotificationService;
}

function createDefaultStore(): WorkerConfigStore {
  const storeMode = process.env.WORKER_STORE ?? 'memory';

  if (storeMode === 'postgres') {
    return new PostgresWorkerStore(pool);
  }

  return new InMemoryWorkerStore();
}

function createDefaultScanner(): SmbScanner {
  const scannerMode = process.env.WORKER_SCANNER ?? 'auto';

  if (scannerMode === 'static') {
    return new StaticSmbScanner({});
  }

  if (scannerMode === 'smb') {
    return new SmbClientScanner();
  }

  if (scannerMode === 'filesystem') {
    return new FilesystemSmbScanner();
  }

  return new AutoSmbScanner();
}

function createDefaultDispatcher(): PrinterDispatcher {
  return new ProviderPrinterDispatcher();
}

function createDefaultOcrProvider(): OcrProvider {
  return new MockOcrProvider();
}

class WorkerPipelineNotifier {
  constructor(private readonly notifications: SmtpNotificationService) {}

  async notify(input: {
    kind: 'SOURCE_FAILURE' | 'JOB_FAILURE';
    source: {
      id: string;
      path: string;
    };
    job?: {
      id: string;
      filePath: string;
      status: string;
    };
    errorMessage: string;
  }): Promise<void> {
    const subject =
      input.kind === 'SOURCE_FAILURE'
        ? `[printo] Source scan failure for ${input.source.path}`
        : `[printo] Print job failure for ${input.job?.filePath ?? input.source.path}`;
    const dedupeKey =
      input.kind === 'SOURCE_FAILURE'
        ? `source:${input.source.id}:${input.errorMessage}`
        : `job:${input.job?.id ?? 'unknown'}:${input.errorMessage}`;
    const textLines = [
      `Event: ${input.kind}`,
      `Source ID: ${input.source.id}`,
      `Source path: ${input.source.path}`,
      input.job?.id ? `Job ID: ${input.job.id}` : '',
      input.job?.filePath ? `Job file: ${input.job.filePath}` : '',
      `Error: ${input.errorMessage}`,
      '',
      `Time: ${new Date().toISOString()}`
    ].filter(Boolean);

    await this.notifications.send({
      category: 'PIPELINE_FAILURE',
      dedupeKey,
      subject,
      text: textLines.join('\n')
    });
  }
}

function createDefaultNotificationService(store: WorkerConfigStore): SmtpNotificationService {
  return new SmtpNotificationService(async () => {
    const settings = await store.getSystemSettings();
    return settings;
  });
}

function createDefaultPipeline(store: WorkerConfigStore, notifications: SmtpNotificationService): WorkerPipeline {
  const scanner = createDefaultScanner();
  const ocrProvider = createDefaultOcrProvider();
  const dispatcher = createDefaultDispatcher();
  const classifier = createDefaultPageClassifier();

  return new WorkerPipeline(store, scanner, ocrProvider, dispatcher, new WorkerPipelineNotifier(notifications), classifier);
}

export function createWorkerApp(options: WorkerAppOptions = {}) {
  const app = express();
  const store = options.store ?? createDefaultStore();
  const notifications = options.notifications ?? createDefaultNotificationService(store);
  const pipeline = options.pipeline ?? createDefaultPipeline(store, notifications);
  const runner = options.runner ?? new WorkerRunner(pipeline);

  app.use(express.json());

  app.get('/health', (_req, res) => {
    res.json({ service: 'worker', status: 'ok' });
  });

  app.get('/pipeline/status', (_req, res) => {
    return res.json({ status: 'ok', runner: runner.getState() });
  });

  app.post('/pipeline/run-once', async (_req, res) => {
    try {
      const summary: PipelineRunSummary = await runner.runOnce();
      return res.json({ status: 'ok', summary, runner: runner.getState() });
    } catch (error) {
      const message = error instanceof Error ? error.message : 'WORKER_PIPELINE_ERROR';
      return res.status(500).json({ status: 'error', error: message, runner: runner.getState() });
    }
  });

  app.get('/pipeline/notifications', async (req, res) => {
    const parsedLimit = Number(req.query.limit ?? 100);
    const limit = Number.isFinite(parsedLimit) ? Math.max(1, Math.min(200, Math.trunc(parsedLimit))) : 100;
    const attempts: NotificationAttemptRecord[] = notifications.listAttempts(limit);
    return res.json(attempts);
  });

  app.post('/pipeline/notifications/test', async (req, res) => {
    const actor = typeof req.body?.actor === 'string' ? req.body.actor.trim() : '';
    const note = typeof req.body?.note === 'string' ? req.body.note.trim() : '';
    const result = await notifications.send({
      category: 'TEST',
      subject: '[printo] SMTP test notification',
      text: [
        'This is a test notification from Printo.',
        actor ? `Actor: ${actor}` : '',
        note ? `Note: ${note}` : '',
        `Time: ${new Date().toISOString()}`
      ]
        .filter(Boolean)
        .join('\n')
    });

    const statusCode = result.status === 'SUCCESS' ? 200 : result.status === 'SKIPPED' ? 409 : 500;
    return res.status(statusCode).json(result);
  });

  app.get('/pipeline/jobs', async (req, res) => {
    const parsedLimit = Number(req.query.limit ?? 100);
    const limit = Number.isFinite(parsedLimit) ? Math.max(1, Math.min(500, Math.trunc(parsedLimit))) : 100;
    const jobs = await store.listPrintJobs(limit);
    return res.json(jobs);
  });

  app.get('/pipeline/jobs/:jobId/pages', async (req, res) => {
    const pages = await store.listPrintJobPages(req.params.jobId);
    return res.json(pages);
  });

  app.post('/pipeline/jobs/:jobId/cancel', async (req, res) => {
    const job = await store.cancelPrintJob(req.params.jobId);
    if (!job) {
      return res.status(404).json({ error: 'PRINT_JOB_NOT_FOUND' });
    }
    return res.json(job);
  });

  app.post('/pipeline/jobs/:jobId/retry', async (req, res) => {
    const existingJob = await store.getPrintJob(req.params.jobId);
    if (!existingJob) {
      return res.status(404).json({ error: 'PRINT_JOB_NOT_FOUND' });
    }
    if (existingJob.status === 'SUCCESS') {
      return res.status(409).json({ error: 'PRINT_JOB_ALREADY_SUCCESSFUL' });
    }

    try {
      const result = await pipeline.retryJob(req.params.jobId);
      return res.json({ job: result.retriedJob, summary: result.summary, runner: runner.getState() });
    } catch (error) {
      const message = error instanceof Error ? error.message : 'WORKER_PIPELINE_ERROR';
      const statusCode = message === 'PRINT_JOB_FILE_NOT_FOUND' ? 409 : 500;
      return res.status(statusCode).json({ job: existingJob, error: message, runner: runner.getState() });
    }
  });

  return { app, runner, store, notifications };
}
