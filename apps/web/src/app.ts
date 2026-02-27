import express from 'express';

export function createWebApp() {
  const app = express();

  app.get('/health', (_req, res) => {
    res.json({ service: 'web', status: 'ok' });
  });

  app.get('/', (_req, res) => {
    res.json({ name: 'printo-web', phase: '0-1 scaffold' });
  });

  return app;
}
