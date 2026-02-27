import { createWorkerApp } from './app.js';

const port = Number(process.env.PORT ?? process.env.WORKER_PORT ?? 5000);
const app = createWorkerApp();

app.listen(port, () => {
  // eslint-disable-next-line no-console
  console.log(JSON.stringify({ service: 'worker', event: 'listening', port }));
});
