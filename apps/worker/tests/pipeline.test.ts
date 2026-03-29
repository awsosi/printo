import { describe, expect, it } from 'vitest';
import {
  InMemoryWorkerStore,
  MockOcrProvider,
  RecordingPrinterDispatcher,
  StaticSmbScanner,
  WorkerPipeline,
  type PipelineNotifier,
  type ScannedFile
} from '../src/pipeline.js';

describe('worker pipeline', () => {
  it('processes masked files, routes pages, and skips duplicates', async () => {
    const store = new InMemoryWorkerStore({
      sources: [
        {
          id: 'source-1',
          ownerUserId: 'user-1',
          ownerGroupId: null,
          path: '\\\\srv\\share',
          domainUsername: 'EXAMPLE\\\\serviceuser',
          secretRef: 'secret/smb/user-1',
          isActive: true
        }
      ],
      masks: [
        {
          id: 'mask-1',
          ownerUserId: 'user-1',
          ownerGroupId: null,
          pattern: 'invoice',
          isRegex: false,
          isActive: true
        }
      ],
      printers: [
        {
          id: 'printer-a4-default',
          name: 'Office A4',
          type: 'A4',
          targetUri: 'ipp://a4.local',
          domainUsername: '',
          secretRef: '',
          isActive: true
        },
        {
          id: 'printer-a4-user',
          name: 'User A4',
          type: 'A4',
          targetUri: 'ipp://user-a4.local',
          domainUsername: '',
          secretRef: '',
          isActive: true
        },
        {
          id: 'printer-thermal-default',
          name: 'GK420d',
          type: 'THERMAL',
          targetUri: 'socket://thermal.local:9100',
          domainUsername: '',
          secretRef: '',
          isActive: true
        },
        {
          id: 'printer-thermal-user',
          name: 'User Thermal',
          type: 'THERMAL',
          targetUri: 'socket://user-thermal.local:9100',
          domainUsername: '',
          secretRef: '',
          isActive: true
        }
      ],
      userPrinterAssignments: [
        {
          userId: 'user-1',
          a4PrinterId: 'printer-a4-user',
          thermalPrinterId: 'printer-thermal-user'
        }
      ],
      routingProfiles: [
        {
          id: 'routing-1',
          name: 'default',
          ownerUserId: null,
          ownerGroupId: null,
          defaultRouteType: 'A4',
          thermalLabelPatterns: ['label'],
          fallbackPrinterId: null,
          samplePdfName: null,
          samplePdfBase64: null,
          visualRules: []
        }
      ],
      ocrGlobalConfig: {
        provider: 'mock',
        config: {
          thermalKeyword: 'label'
        }
      }
    });

    const files: ScannedFile[] = [
      {
        sourceId: 'source-1',
        path: '/in/invoice-123.pdf',
        content: 'label page\nregular page',
        modifiedAt: new Date('2026-02-27T15:00:00.000Z')
      },
      {
        sourceId: 'source-1',
        path: '/in/notes.txt',
        content: 'not matched by mask',
        modifiedAt: new Date('2026-02-27T15:05:00.000Z')
      }
    ];

    const scanner = new StaticSmbScanner({ 'source-1': files });
    const ocrProvider = new MockOcrProvider();
    const dispatcher = new RecordingPrinterDispatcher();
    const pipeline = new WorkerPipeline(store, scanner, ocrProvider, dispatcher);

    const firstRun = await pipeline.runOnce();
    expect(firstRun.filesDiscovered).toBe(2);
    expect(firstRun.filesMatched).toBe(1);
    expect(firstRun.filesProcessed).toBe(1);
    expect(firstRun.filesSkippedDedup).toBe(0);
    expect(firstRun.jobsCreated).toBe(1);
    expect(firstRun.pageDispatches).toBe(2);

    expect(dispatcher.calls).toHaveLength(2);
    expect(dispatcher.calls[0]?.routeType).toBe('THERMAL');
    expect(dispatcher.calls[0]?.printer.id).toBe('printer-thermal-user');
    expect(dispatcher.calls[1]?.routeType).toBe('A4');
    expect(dispatcher.calls[1]?.printer.id).toBe('printer-a4-user');

    const secondRun = await pipeline.runOnce();
    expect(secondRun.filesMatched).toBe(1);
    expect(secondRun.filesProcessed).toBe(0);
    expect(secondRun.filesSkippedDedup).toBe(1);
    expect(dispatcher.calls).toHaveLength(2);
  });

  it('continues processing when one source scan fails', async () => {
    const store = new InMemoryWorkerStore({
      sources: [
        {
          id: 'source-ok',
          ownerUserId: 'user-1',
          ownerGroupId: null,
          path: '/ok',
          domainUsername: 'EXAMPLE\\\\serviceuser',
          secretRef: 'secret/smb/ok',
          isActive: true
        },
        {
          id: 'source-fail',
          ownerUserId: 'user-1',
          ownerGroupId: null,
          path: '/fail',
          domainUsername: 'EXAMPLE\\\\serviceuser',
          secretRef: 'secret/smb/fail',
          isActive: true
        }
      ],
      printers: [
        {
          id: 'printer-a4',
          name: 'Office A4',
          type: 'A4',
          targetUri: 'ipp://a4.local',
          domainUsername: '',
          secretRef: '',
          isActive: true
        }
      ]
    });

    const scanner = {
      scanSource: async (source: { id: string }) => {
        if (source.id === 'source-fail') {
          throw new Error('SMB_SCAN_FAILED');
        }

        return [
          {
            sourceId: 'source-ok',
            path: '/ok/doc.pdf',
            content: 'plain page',
            modifiedAt: new Date('2026-02-27T15:10:00.000Z')
          }
        ];
      }
    };

    const notifications: Array<{ kind: string; errorMessage: string }> = [];
    const notifier: PipelineNotifier = {
      notify: async (input) => {
        notifications.push({ kind: input.kind, errorMessage: input.errorMessage });
      }
    };

    const pipeline = new WorkerPipeline(store, scanner, new MockOcrProvider(), new RecordingPrinterDispatcher(), notifier);
    const summary = await pipeline.runOnce();

    expect(summary.sourcesScanned).toBe(2);
    expect(summary.filesProcessed).toBe(1);
    expect(summary.failures).toBe(1);
    expect(notifications).toEqual([{ kind: 'SOURCE_FAILURE', errorMessage: 'SMB_SCAN_FAILED' }]);
  });

  it('routes pages from stored visual rectangle rules', async () => {
    const store = new InMemoryWorkerStore({
      sources: [
        {
          id: 'source-1',
          ownerUserId: null,
          ownerGroupId: null,
          path: '/in',
          domainUsername: 'EXAMPLE\\\\serviceuser',
          secretRef: 'secret/smb/source-1',
          isActive: true
        }
      ],
      printers: [
        {
          id: 'printer-a4',
          name: 'Office A4',
          type: 'A4',
          targetUri: 'ipp://a4.local',
          domainUsername: '',
          secretRef: '',
          isActive: true
        },
        {
          id: 'printer-thermal',
          name: 'Label',
          type: 'THERMAL',
          targetUri: 'socket://thermal.local:9100',
          domainUsername: '',
          secretRef: '',
          isActive: true
        }
      ],
      routingProfiles: [
        {
          id: 'routing-visual',
          name: 'visual',
          ownerUserId: null,
          ownerGroupId: null,
          defaultRouteType: 'A4',
          thermalLabelPatterns: [],
          fallbackPrinterId: null,
          samplePdfName: 'sample.pdf',
          samplePdfBase64: 'base64',
          visualRules: [
            {
              id: 'rule-1',
              samplePageNumber: 1,
              routeType: 'THERMAL',
              matchMode: 'CONTAINS',
              expectedText: 'ship label',
              expectedWords: ['ship', 'label'],
              rect: { x: 0.1, y: 0.1, width: 0.3, height: 0.08 }
            }
          ]
        }
      ]
    });

    const scanner = new StaticSmbScanner({
      'source-1': [
        {
          sourceId: 'source-1',
          path: '/in/doc.pdf',
          content: 'ignored',
          modifiedAt: new Date('2026-02-27T15:20:00.000Z')
        }
      ]
    });

    const ocrProvider = {
      analyze: async () => ({
        pages: [
          {
            pageNumber: 1,
            labels: [],
            text: '',
            pageWidth: 1000,
            pageHeight: 1000,
            textItems: [{ text: 'ship label', x: 120, y: 120, width: 90, height: 20 }]
          },
          {
            pageNumber: 2,
            labels: [],
            text: '',
            pageWidth: 1000,
            pageHeight: 1000,
            textItems: [{ text: 'invoice', x: 120, y: 120, width: 60, height: 20 }]
          }
        ]
      })
    };

    const dispatcher = new RecordingPrinterDispatcher();
    const pipeline = new WorkerPipeline(store, scanner, ocrProvider, dispatcher);
    const summary = await pipeline.runOnce();

    expect(summary.filesProcessed).toBe(1);
    expect(dispatcher.calls).toHaveLength(2);
    expect(dispatcher.calls[0]?.routeType).toBe('THERMAL');
    expect(dispatcher.calls[1]?.routeType).toBe('A4');
  });

  it('notifies on job failure', async () => {
    const store = new InMemoryWorkerStore({
      sources: [
        {
          id: 'source-1',
          ownerUserId: null,
          ownerGroupId: null,
          path: '/in',
          domainUsername: '',
          secretRef: '',
          isActive: true
        }
      ],
      printers: [
        {
          id: 'printer-a4',
          name: 'Office A4',
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
          path: '/in/doc.pdf',
          content: 'page 1',
          modifiedAt: new Date('2026-02-27T15:30:00.000Z')
        }
      ]
    });

    const dispatcher = {
      dispatch: async () => {
        throw new Error('PRINTER_OFFLINE');
      }
    };
    const notifications: Array<{ kind: string; errorMessage: string; filePath?: string }> = [];
    const notifier: PipelineNotifier = {
      notify: async (input) => {
        notifications.push({ kind: input.kind, errorMessage: input.errorMessage, filePath: input.job?.filePath });
      }
    };

    const pipeline = new WorkerPipeline(store, scanner, new MockOcrProvider(), dispatcher, notifier);
    const summary = await pipeline.runOnce();

    expect(summary.failures).toBe(1);
    expect(notifications).toHaveLength(1);
    expect(notifications[0]).toEqual({
      kind: 'JOB_FAILURE',
      errorMessage: 'PRINTER_OFFLINE',
      filePath: '/in/doc.pdf'
    });
  });
});
