import { defineConfig } from '@playwright/test';

const isCI = Boolean(process.env.CI);

export default defineConfig({
  testDir: './tests/e2e',
  timeout: 30_000,
  forbidOnly: isCI,
  retries: isCI ? 1 : 0,
  workers: isCI ? 1 : undefined,
  reporter: isCI ? [['github'], ['list']] : [['list']],
  use: {
    trace: 'retain-on-failure'
  },
  webServer: [
    {
      command: 'npm run -w @printo/api dev',
      url: 'http://127.0.0.1:4000/health',
      reuseExistingServer: !isCI,
      stdout: 'pipe',
      stderr: 'pipe'
    },
    {
      command: 'npm run -w @printo/web dev',
      url: 'http://127.0.0.1:3000/health',
      reuseExistingServer: !isCI,
      stdout: 'pipe',
      stderr: 'pipe'
    }
  ]
});
