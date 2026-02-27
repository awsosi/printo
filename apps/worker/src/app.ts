import express from 'express';

export function createWorkerApp() {
  const app = express();

  app.get('/health', (_req, res) => {
    res.json({ service: 'worker', status: 'ok' });
  });

  return app;
}
