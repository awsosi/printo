import 'dotenv/config';
import { createApiApp } from './app.js';
import { pool } from './db/pool.js';
import { PostgresAuthStore } from './store/postgres-auth-store.js';
import { PostgresAgentStore } from './agents/store.js';
import { PostgresAdvisoryLock, RetentionScheduler } from './agents/retention.js';

const port = Number(process.env.PORT ?? process.env.API_PORT ?? 4000);
const host = process.env.HOST ?? '0.0.0.0';
const agentStore = new PostgresAgentStore(pool);
const app = createApiApp(new PostgresAuthStore(pool), agentStore);

/**
 * Retention runs on a schedule, not only when an administrator presses the button.
 *
 * Advisory-locked so a scaled-out deployment sweeps once rather than once per replica, and
 * disabled by setting the interval to 0 for deployments that prefer an external cron.
 */
const retentionHours = Number(process.env.RETENTION_INTERVAL_HOURS ?? 24);
const retention = new RetentionScheduler(agentStore, {
  intervalMs: retentionHours * 60 * 60 * 1000,
  lock: new PostgresAdvisoryLock(pool)
});

if (Number.isFinite(retentionHours) && retentionHours > 0) {
  retention.start();
}

const server = app.listen(port, host, () => {
  // eslint-disable-next-line no-console
  console.log(JSON.stringify({ service: 'api', event: 'listening', port, host }));
});

for (const signal of ['SIGTERM', 'SIGINT'] as const) {
  process.once(signal, () => {
    retention.stop();
    server.close(() => {
      void pool.end();
    });
  });
}
