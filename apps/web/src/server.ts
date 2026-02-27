import { createWebApp } from './app.js';

const port = Number(process.env.PORT ?? process.env.WEB_PORT ?? 3000);
const app = createWebApp();

app.listen(port, () => {
  // eslint-disable-next-line no-console
  console.log(JSON.stringify({ service: 'web', event: 'listening', port }));
});
