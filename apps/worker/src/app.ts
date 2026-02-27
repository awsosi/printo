import express from 'express';
import { pool } from './db/pool.js';
import { LoggingPrinterDispatcher } from './dispatch/logging-printer-dispatcher.js';
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
import { FilesystemSmbScanner } from './scanner/filesystem-smb-scanner.js';
import { PostgresWorkerStore } from './store/postgres-worker-store.js';

export interface WorkerAppOptions {
  pipeline?: WorkerPipeline;
  runner?: WorkerRunner;
}

function createDefaultStore(): WorkerConfigStore {
  const storeMode = process.env.WORKER_STORE ?? 'memory';

  if (storeMode === 'postgres') {
    return new PostgresWorkerStore(pool);
  }

  return new InMemoryWorkerStore();
}

function createDefaultScanner(): SmbScanner {
  const scannerMode = process.env.WORKER_SCANNER ?? 'filesystem';

  if (scannerMode === 'static') {
    return new StaticSmbScanner({});
  }

  return new FilesystemSmbScanner();
}

function createDefaultDispatcher(): PrinterDispatcher {
  return new LoggingPrinterDispatcher();
}

function createDefaultOcrProvider(): OcrProvider {
  return new MockOcrProvider();
}

function createDefaultPipeline(): WorkerPipeline {
  const store = createDefaultStore();
  const scanner = createDefaultScanner();
  const ocrProvider = createDefaultOcrProvider();
  const dispatcher = createDefaultDispatcher();

  return new WorkerPipeline(store, scanner, ocrProvider, dispatcher);
}

export function createWorkerApp(options: WorkerAppOptions = {}) {
  const app = express();
  const pipeline = options.pipeline ?? createDefaultPipeline();
  const runner = options.runner ?? new WorkerRunner(pipeline);

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

  return { app, runner };
}
