import request from 'supertest';
import { describe, expect, it, vi } from 'vitest';
import { createWorkerApp } from '../src/app.js';
import { SmtpNotificationService } from '../src/smtp.js';
import { InMemoryWorkerStore, MockOcrProvider, StaticSmbScanner, WorkerPipeline } from '../src/pipeline.js';

describe('worker app', () => {
  it('returns health', async () => {
    const { app } = createWorkerApp();
    const res = await request(app).get('/health');
    expect(res.status).toBe(200);
    expect(res.body).toEqual({ service: 'worker', status: 'ok' });
  });

  it('runs empty pipeline safely and exposes runner status', async () => {
    const { app } = createWorkerApp();

    const before = await request(app).get('/pipeline/status');
    expect(before.status).toBe(200);
    expect(before.body.runner.runCount).toBe(0);

    const run = await request(app).post('/pipeline/run-once');
    expect(run.status).toBe(200);
    expect(run.body.status).toBe('ok');
    expect(run.body.summary.filesProcessed).toBe(0);

    const after = await request(app).get('/pipeline/status');
    expect(after.status).toBe(200);
    expect(after.body.runner.runCount).toBe(1);
    expect(after.body.runner.lastSummary.filesProcessed).toBe(0);
    expect(after.body.runner.lastError).toBeNull();
  });

  it('lists notification attempts and allows test send', async () => {
    const notifications = new SmtpNotificationService(
      async () => ({
        smtpEnabled: true,
        smtpHost: 'smtp.example.test',
        smtpPort: 25,
        smtpSecure: false,
        smtpUsername: '',
        smtpSecretRef: '',
        smtpFrom: 'printo@example.test',
        smtpTo: ['admin@example.test']
      }),
      {
        sendMail: vi.fn(async () => undefined)
      }
    );
    const { app } = createWorkerApp({ notifications });

    const send = await request(app).post('/pipeline/notifications/test').send({ actor: 'admin' });
    expect(send.status).toBe(200);
    expect(send.body.status).toBe('SUCCESS');

    const list = await request(app).get('/pipeline/notifications?limit=10');
    expect(list.status).toBe(200);
    expect(list.body).toHaveLength(1);
    expect(list.body[0].category).toBe('TEST');
  });

  it('retries incomplete jobs and rejects retrying successful jobs', async () => {
    const store = new InMemoryWorkerStore({
      sources: [
        {
          id: 'source-1',
          ownerUserId: null,
          ownerGroupId: null,
          path: '/in',
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
        {
          id: 'printer-a4',
          name: 'A4',
          type: 'A4',
          targetUri: 'ipp://a4.local',
          domainUsername: '',
          secretRef: '',
          isActive: true
        }
      ]
    });
    const scanner = new StaticSmbScanner({
      'source-1': [
        {
          sourceId: 'source-1',
          path: '/in/retry-me.pdf',
          content: 'page 1',
          modifiedAt: new Date('2026-04-02T10:00:00.000Z')
        }
      ]
    });
    let failedOnce = false;
    const dispatcher = {
      dispatch: vi.fn(async () => {
        if (!failedOnce) {
          failedOnce = true;
          throw new Error('PRINTER_BUSY');
        }
      })
    };
    const pipeline = new WorkerPipeline(store, scanner, new MockOcrProvider(), dispatcher);
    const { app } = createWorkerApp({ pipeline, store });

    const firstRun = await request(app).post('/pipeline/run-once');
    expect(firstRun.status).toBe(200);
    const failedJobId = store.printJobs[0]!.id;

    const retry = await request(app).post('/pipeline/jobs/' + failedJobId + '/retry');
    expect(retry.status).toBe(200);
    expect(retry.body.job.status).toBe('SUCCESS');
    expect(retry.body.summary.pageDispatches).toBe(1);

    const successfulJobId = retry.body.job.id as string;
    const retrySuccessful = await request(app).post('/pipeline/jobs/' + successfulJobId + '/retry');
    expect(retrySuccessful.status).toBe(409);
    expect(retrySuccessful.body.error).toBe('PRINT_JOB_ALREADY_SUCCESSFUL');
  });
});
