import request from 'supertest';
import { beforeEach, describe, expect, it } from 'vitest';
import { createWorkerApp } from '../src/app.js';
import { workerMetrics, WorkerMetrics } from '../src/metrics.js';
import {
  InMemoryWorkerStore,
  MockOcrProvider,
  RecordingPrinterDispatcher,
  StaticSmbScanner,
  WorkerPipeline
} from '../src/pipeline.js';

describe('WorkerMetrics', () => {
  it('renders counters and histograms in Prometheus text format', () => {
    const metrics = new WorkerMetrics();
    metrics.jobsTotal.inc({ status: 'SUCCESS' });
    metrics.jobsTotal.inc({ status: 'SUCCESS' });
    metrics.jobsTotal.inc({ status: 'FAILURE' });
    metrics.pagesClassifiedTotal.inc({ page_class: 'OUTGOING_LABEL_THERMAL', classifier: 'heuristic' });
    metrics.classificationConfidence.observe(0.85);
    metrics.classificationConfidence.observe(0.25);

    const output = metrics.render();
    expect(output).toContain('# TYPE printo_jobs_total counter');
    expect(output).toContain('printo_jobs_total{status="SUCCESS"} 2');
    expect(output).toContain('printo_jobs_total{status="FAILURE"} 1');
    expect(output).toContain('printo_pages_classified_total{classifier="heuristic",page_class="OUTGOING_LABEL_THERMAL"} 1');
    expect(output).toContain('printo_classification_confidence_bucket{le="0.9"} 2');
    expect(output).toContain('printo_classification_confidence_bucket{le="0.3"} 1');
    expect(output).toContain('printo_classification_confidence_count 2');
  });
});

describe('worker /metrics endpoint', () => {
  beforeEach(() => {
    workerMetrics.reset();
  });

  it('exposes pipeline metrics after a run', async () => {
    const store = new InMemoryWorkerStore({
      sources: [
        {
          id: 'source-1',
          ownerUserId: null,
          ownerGroupId: null,
          path: '\\\\srv\\share',
          domainUsername: '',
          secretRef: '',
          printerDomainUsername: '',
          printerSecretRef: '',
          routingProfileId: null,
          a4PrinterId: null,
          thermalPrinterId: null,
          includeFilenamePatterns: [],
          excludeFilenamePatterns: [],
          isActive: true
        }
      ],
      printers: [
        { id: 'p-a4', name: 'A4', type: 'A4', targetUri: 'cups://OfficeA4', domainUsername: '', secretRef: '', isActive: true },
        { id: 'p-th', name: 'Zebra', type: 'THERMAL', targetUri: 'cups://Zebra', domainUsername: '', secretRef: '', isActive: true }
      ]
    });
    const scanner = new StaticSmbScanner({
      'source-1': [
        {
          sourceId: 'source-1',
          path: '/in/mixed.txt',
          content:
            'Faktura VAT suma brutto\nDHL EXPRESS WORLDWIDE Ship to Tracking number Waybill JJD0099887766554433',
          modifiedAt: new Date('2026-07-17T09:00:00.000Z')
        }
      ]
    });
    const pipeline = new WorkerPipeline(store, scanner, new MockOcrProvider(), new RecordingPrinterDispatcher());
    const { app } = createWorkerApp({ pipeline, store });

    await pipeline.runOnce();

    const response = await request(app).get('/metrics');
    expect(response.status).toBe(200);
    expect(response.headers['content-type']).toContain('text/plain');
    expect(response.text).toContain('printo_jobs_total{status="SUCCESS"} 1');
    expect(response.text).toContain('page_class="OUTGOING_LABEL_THERMAL"');
    expect(response.text).toContain('printo_pages_routed_total{decided_by="CLASSIFICATION",route_type="THERMAL"} 1');
    expect(response.text).toContain('printo_page_dispatch_total{route_type="A4",status="SUCCESS"} 1');
    expect(response.text).toContain('printo_files_processed_total 1');
  });
});
