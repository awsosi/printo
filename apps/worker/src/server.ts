import { createWorkerApp } from './app.js';
import { pool } from './db/pool.js';

const port = Number(process.env.PORT ?? process.env.WORKER_PORT ?? 5000);
const intervalMs = Number(process.env.WORKER_POLL_INTERVAL_MS ?? 60_000);
const autostart = process.env.WORKER_AUTOSTART !== 'false';

const { app, runner } = createWorkerApp();

if (autostart) {
  runner.start(intervalMs);
}

const server = app.listen(port, () => {
  // eslint-disable-next-line no-console
  console.log(JSON.stringify({ service: 'worker', event: 'listening', port, autostart, intervalMs }));
});

async function shutdown() {
  runner.stop();

  await new Promise<void>((resolve) => {
    server.close(() => resolve());
  });

  await pool.end();
  process.exit(0);
}

process.on('SIGINT', () => {
  void shutdown();
});

process.on('SIGTERM', () => {
  void shutdown();
});
