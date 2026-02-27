import { expect, test } from '@playwright/test';

test('web + api health endpoints are reachable', async ({ request }) => {
  const webHealthResponse = await request.get('http://127.0.0.1:3000/health');
  expect(webHealthResponse.ok()).toBeTruthy();
  await expect(webHealthResponse.json()).resolves.toEqual({
    service: 'web',
    status: 'ok'
  });

  const apiHealthResponse = await request.get('http://127.0.0.1:4000/health');
  expect(apiHealthResponse.ok()).toBeTruthy();
  await expect(apiHealthResponse.json()).resolves.toEqual({
    service: 'api',
    status: 'ok'
  });
});
