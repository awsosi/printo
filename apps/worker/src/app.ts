import express from 'express';
import {
  InMemoryWorkerStore,
  MockOcrProvider,
  RecordingPrinterDispatcher,
  StaticSmbScanner,
  WorkerPipeline,
  type PipelineRunSummary
} from './pipeline.js';

export interface WorkerAppOptions {
  pipeline?: WorkerPipeline;
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

  app.get('/health', (_req, res) => {
    res.json({ service: 'worker', status: 'ok' });
  });

  app.post('/pipeline/run-once', async (_req, res) => {
    try {
      const summary: PipelineRunSummary = await pipeline.runOnce();
      return res.json({ status: 'ok', summary });
    } catch (error) {
      const message = error instanceof Error ? error.message : 'WORKER_PIPELINE_ERROR';
      return res.status(500).json({ status: 'error', error: message });
    }
  });

  return app;
}
