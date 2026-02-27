import type { PipelineRunSummary, WorkerPipeline } from './pipeline.js';

export interface WorkerRunnerState {
  isRunning: boolean;
  runCount: number;
  lastRunStartedAt: string | null;
  lastRunFinishedAt: string | null;
  lastSummary: PipelineRunSummary | null;
  lastError: string | null;
}

function toIsoOrNull(value: Date | null): string | null {
  return value ? value.toISOString() : null;
}

export class WorkerRunner {
  private timer: NodeJS.Timeout | null = null;
  private running = false;
  private inFlightRun: Promise<PipelineRunSummary> | null = null;

  private runCount = 0;
  private lastRunStartedAt: Date | null = null;
  private lastRunFinishedAt: Date | null = null;
  private lastSummary: PipelineRunSummary | null = null;
  private lastError: string | null = null;

  constructor(private readonly pipeline: WorkerPipeline) {}

  start(intervalMs: number): void {
    if (this.timer) {
      return;
    }

    this.timer = setInterval(() => {
      void this.runOnce();
    }, intervalMs);
  }

  stop(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  async runOnce(): Promise<PipelineRunSummary> {
    if (this.inFlightRun) {
      return this.inFlightRun;
    }

    this.lastRunStartedAt = new Date();
    this.running = true;

    this.inFlightRun = this.pipeline
      .runOnce()
      .then((summary) => {
        this.runCount += 1;
        this.lastSummary = summary;
        this.lastError = null;
        return summary;
      })
      .catch((error) => {
        this.runCount += 1;
        this.lastError = error instanceof Error ? error.message : 'WORKER_PIPELINE_ERROR';
        throw error;
      })
      .finally(() => {
        this.running = false;
        this.lastRunFinishedAt = new Date();
        this.inFlightRun = null;
      });

    return this.inFlightRun;
  }

  getState(): WorkerRunnerState {
    return {
      isRunning: this.running,
      runCount: this.runCount,
      lastRunStartedAt: toIsoOrNull(this.lastRunStartedAt),
      lastRunFinishedAt: toIsoOrNull(this.lastRunFinishedAt),
      lastSummary: this.lastSummary,
      lastError: this.lastError
    };
  }
}
