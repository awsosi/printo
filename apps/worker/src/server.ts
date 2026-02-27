import { createWorkerApp } from './app.js';

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

function shutdown() {
  runner.stop();
  server.close(() => {
    process.exit(0);
  });
}

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
