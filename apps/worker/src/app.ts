import express from 'express';
import {
  InMemoryWorkerStore,
  MockOcrProvider,
  RecordingPrinterDispatcher,
  StaticSmbScanner,
  WorkerPipeline,
  type PipelineRunSummary
} from './pipeline.js';
import { WorkerRunner } from './runner.js';

export interface WorkerAppOptions {
  pipeline?: WorkerPipeline;
  runner?: WorkerRunner;
}

function createDefaultPipeline(): WorkerPipeline {
  const store = new InMemoryWorkerStore();
  const scanner = new StaticSmbScanner({});
  const ocrProvider = new MockOcrProvider();
  const dispatcher = new RecordingPrinterDispatcher();

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
