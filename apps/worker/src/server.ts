import { createWorkerApp } from './app.js';
import { pool } from './db/pool.js';

const port = Number(process.env.PORT ?? process.env.WORKER_PORT ?? 5000);
const host = process.env.HOST ?? '0.0.0.0';
const fallbackIntervalMs = Number(process.env.WORKER_POLL_INTERVAL_MS ?? 60_000);
const autostart = process.env.WORKER_AUTOSTART !== 'false';

const { app, runner, store } = createWorkerApp();

let server: ReturnType<typeof app.listen> | null = null;

async function start() {
  let intervalMs = fallbackIntervalMs;

  try {
    const settings = await store.getSystemSettings();
    if (Number.isFinite(settings.workerPollIntervalMs) && settings.workerPollIntervalMs >= 1000) {
      intervalMs = settings.workerPollIntervalMs;
    }
  } catch {
    intervalMs = fallbackIntervalMs;
  }

  if (autostart) {
    runner.start(intervalMs);
  }

  server = app.listen(port, host, () => {
    // eslint-disable-next-line no-console
    console.log(JSON.stringify({ service: 'worker', event: 'listening', port, host, autostart, intervalMs }));
  });
}

async function shutdown() {
  runner.stop();

  if (server) {
    await new Promise<void>((resolve) => {
      server?.close(() => resolve());
    });
  }

  await pool.end();
  process.exit(0);
}

process.on('SIGINT', () => {
  void shutdown();
});

process.on('SIGTERM', () => {
  void shutdown();
});

void start();
