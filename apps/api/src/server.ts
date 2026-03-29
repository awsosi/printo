import 'dotenv/config';
import { createApiApp } from './app.js';
import { pool } from './db/pool.js';
import { PostgresAuthStore } from './store/postgres-auth-store.js';

const port = Number(process.env.PORT ?? process.env.API_PORT ?? 4000);
const host = process.env.HOST ?? '0.0.0.0';
const app = createApiApp(new PostgresAuthStore(pool));

app.listen(port, host, () => {
  // eslint-disable-next-line no-console
  console.log(JSON.stringify({ service: 'api', event: 'listening', port, host }));
});
