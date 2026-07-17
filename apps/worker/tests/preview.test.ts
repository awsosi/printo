import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { join, dirname } from 'node:path';
import request from 'supertest';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createWorkerApp } from '../src/app.js';
import { InMemoryWorkerStore } from '../src/pipeline.js';

const fixturePath = join(dirname(fileURLToPath(import.meta.url)), '../../../fixtures/intake/mixed-carriers.pdf');

function makeApp(fetchImpl?: typeof fetch) {
  return createWorkerApp({ store: new InMemoryWorkerStore(), fetchImpl }).app;
}

afterEach(() => {
  vi.unstubAllEnvs();
});

describe('POST /pipeline/preview/classification', () => {
  it('classifies and routes every page of an uploaded PDF', async () => {
    const pdf = await readFile(fixturePath);

    const response = await request(makeApp())
      .post('/pipeline/preview/classification')
      .send({ pdfBase64: pdf.toString('base64') });

    expect(response.status).toBe(200);
    expect(response.body.pages).toHaveLength(5);
    expect(response.body.pages.map((page: { pageClass: string }) => page.pageClass)).toEqual([
      'DOCUMENT_A4',
      'OUTGOING_LABEL_THERMAL',
      'DOCUMENT_A4',
      'RETURN_LABEL_A4',
      'OUTGOING_LABEL_THERMAL'
    ]);
    expect(response.body.pages.map((page: { routeType: string }) => page.routeType)).toEqual([
      'A4',
      'THERMAL',
      'A4',
      'A4',
      'THERMAL'
    ]);
    expect(response.body.pages[1].carrier).toBe('DHL');
    expect(response.body.pages[1].decidedBy).toBe('CLASSIFICATION');
    expect(response.body.pages[0].decidedBy).toBe('DEFAULT');
    expect(typeof response.body.pages[1].confidence).toBe('number');
    expect(Array.isArray(response.body.pages[1].evidence)).toBe(true);
  });

  it('applies submitted profile settings (thermal patterns and confidence gates)', async () => {
    const pdf = await readFile(fixturePath);

    const response = await request(makeApp())
      .post('/pipeline/preview/classification')
      .send({
        pdfBase64: pdf.toString('base64'),
        profile: {
          defaultRouteType: 'A4',
          // "faktura" appears on the invoice page — explicit patterns beat classification.
          thermalLabelPatterns: ['faktura'],
          classificationRoutes: [
            { pageClass: 'OUTGOING_LABEL_THERMAL', routeType: 'THERMAL', printerId: null, minConfidence: 0.99 }
          ]
        }
      });

    expect(response.status).toBe(200);
    const pages = response.body.pages as Array<{ pageNumber: number; routeType: string; decidedBy: string }>;
    expect(pages[0].routeType).toBe('THERMAL');
    expect(pages[0].decidedBy).toBe('THERMAL_PATTERN');
    // DHL page is fully confident (1.0) so it passes even a 0.99 gate.
    expect(pages[1].routeType).toBe('THERMAL');
    // Return label has no matching route in the custom set → falls to default A4.
    expect(pages[3].routeType).toBe('A4');
    expect(pages[3].decidedBy).toBe('DEFAULT');
  });

  it('rejects requests without a PDF', async () => {
    const response = await request(makeApp()).post('/pipeline/preview/classification').send({});
    expect(response.status).toBe(400);
    expect(response.body.error).toBe('INVALID_INPUT');
  });
});

describe('GET /pipeline/vision-status', () => {
  it('reports unconfigured state without a vision URL', async () => {
    vi.stubEnv('WORKER_VISION_URL', '');
    const response = await request(makeApp()).get('/pipeline/vision-status');

    expect(response.status).toBe(200);
    expect(response.body).toEqual({ configured: false, mode: 'heuristic', healthy: null, backends: null });
  });

  it('reports healthy vision service with backends', async () => {
    vi.stubEnv('WORKER_VISION_URL', 'http://vision:6000');
    const fetchImpl = (async (url: RequestInfo | URL) => {
      expect(String(url)).toBe('http://vision:6000/health');
      return new Response(
        JSON.stringify({ service: 'vision', status: 'ok', backends: { pdf_rasterizer: true, barcodes: true, ocr: false } }),
        { status: 200, headers: { 'content-type': 'application/json' } }
      );
    }) as typeof fetch;

    const response = await request(makeApp(fetchImpl)).get('/pipeline/vision-status');

    expect(response.status).toBe(200);
    expect(response.body.configured).toBe(true);
    expect(response.body.mode).toBe('auto');
    expect(response.body.healthy).toBe(true);
    expect(response.body.backends).toEqual({ pdf_rasterizer: true, barcodes: true, ocr: false });
  });

  it('reports unhealthy when the vision service is unreachable', async () => {
    vi.stubEnv('WORKER_VISION_URL', 'http://vision:6000');
    const fetchImpl = (async () => {
      throw new Error('ECONNREFUSED');
    }) as typeof fetch;

    const response = await request(makeApp(fetchImpl)).get('/pipeline/vision-status');

    expect(response.status).toBe(200);
    expect(response.body).toEqual({ configured: true, mode: 'auto', healthy: false, backends: null });
  });
});
