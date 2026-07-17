import { createRequire } from 'node:module';
import { matchPdfPagesBySnippet } from '@printo/shared';
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

const require = createRequire(import.meta.url);
// The native canvas module is optional now; the image-snippet test below is skipped
// on hosts where it is not installed (matching the lazy load in @printo/shared).
type CanvasModule = {
  createCanvas(width: number, height: number, type?: 'pdf'): {
    getContext(kind: '2d'): {
      fillStyle: string;
      fillRect(x: number, y: number, width: number, height: number): void;
      beginPath(): void;
      arc(x: number, y: number, radius: number, startAngle: number, endAngle: number): void;
      fill(): void;
      drawImage(
        image: { width: number; height: number },
        sx: number,
        sy: number,
        sw: number,
        sh: number,
        dx: number,
        dy: number,
        dw: number,
        dh: number
      ): void;
      getImageData(x: number, y: number, width: number, height: number): { data: Uint8ClampedArray };
    };
    toBuffer(input?: string): Buffer;
  };
};

function tryLoadCanvas(): CanvasModule | null {
  try {
    return require('canvas') as CanvasModule;
  } catch {
    return null;
  }
}

const canvasModule = tryLoadCanvas();
const pdfjs = require('pdfjs-dist/legacy/build/pdf.js') as {
  getDocument(input: {
    data: Uint8Array;
    useWorkerFetch: boolean;
    isEvalSupported: boolean;
    disableFontFace: boolean;
    standardFontDataUrl: string;
  }): {
    promise: Promise<{
      getPage(pageNumber: number): Promise<{
        getViewport(input: { scale: number }): { width: number; height: number };
        render(input: { canvasContext: unknown; viewport: { width: number; height: number } }): { promise: Promise<void> };
      }>;
      destroy(): Promise<void>;
    }>;
  };
};

function rgbaVariance(data: Uint8ClampedArray): number {
  let sum = 0;
  let sumSquares = 0;
  let count = 0;

  for (let index = 0; index < data.length; index += 4) {
    const value = 0.299 * data[index]! + 0.587 * data[index + 1]! + 0.114 * data[index + 2]!;
    sum += value;
    sumSquares += value * value;
    count += 1;
  }

  if (count === 0) {
    return 0;
  }

  return sumSquares / count - (sum / count) * (sum / count);
}

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
          printerDomainUsername: '',
          printerSecretRef: '',
          defaultRouteType: 'A4',
          thermalLabelPatterns: ['label'],
          fallbackPrinterId: null,
          samplePdfName: null,
          samplePdfBase64: null,
          snippetBase64: null,
          matchThreshold: 0.88,
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
          printerDomainUsername: '',
          printerSecretRef: '',
          routingProfileId: null,
          a4PrinterId: null,
          thermalPrinterId: null,
          includeFilenamePatterns: [],
          excludeFilenamePatterns: [],
          isActive: true
        },
        {
          id: 'source-fail',
          ownerUserId: 'user-1',
          ownerGroupId: null,
          path: '/fail',
          domainUsername: 'EXAMPLE\\\\serviceuser',
          secretRef: 'secret/smb/fail',
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
          snippetBase64: null,
          matchThreshold: 0.88,
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

  it.skipIf(!canvasModule)('routes matched PDF pages to thermal using image snippets', async () => {
    const { createCanvas } = canvasModule!;
    const pdfCanvas = createCanvas(420, 420, 'pdf');
    const pdfContext = pdfCanvas.getContext('2d');
    pdfContext.fillStyle = '#ffffff';
    pdfContext.fillRect(0, 0, 420, 420);
    pdfContext.fillStyle = '#111111';
    pdfContext.fillRect(40, 40, 80, 80);
    pdfContext.beginPath();
    pdfContext.arc(260, 260, 34, 0, Math.PI * 2);
    pdfContext.fill();
    const pdfBuffer = pdfCanvas.toBuffer('application/pdf');
    const loadingTask = pdfjs.getDocument({
      data: new Uint8Array(pdfBuffer),
      useWorkerFetch: false,
      isEvalSupported: false,
      disableFontFace: true,
      standardFontDataUrl: ''
    });
    const document = await loadingTask.promise;
    const page = await document.getPage(1);
    const baseViewport = page.getViewport({ scale: 1 });
    const viewport = page.getViewport({ scale: 320 / baseViewport.width });
    const pageCanvas = createCanvas(Math.round(viewport.width), Math.round(viewport.height));
    const pageContext = pageCanvas.getContext('2d');
    await page.render({ canvasContext: pageContext, viewport }).promise;

    let bestRect = { x: 0, y: 0, size: 96, variance: -1 };
    for (let y = 0; y <= pageCanvas.height - 96; y += 16) {
      for (let x = 0; x <= pageCanvas.width - 96; x += 16) {
        const variance = rgbaVariance(pageContext.getImageData(x, y, 96, 96).data);
        if (variance > bestRect.variance) {
          bestRect = { x, y, size: 96, variance };
        }
      }
    }

    const snippetCanvas = createCanvas(bestRect.size, bestRect.size);
    const snippetContext = snippetCanvas.getContext('2d');
    snippetContext.drawImage(pageCanvas, bestRect.x, bestRect.y, bestRect.size, bestRect.size, 0, 0, bestRect.size, bestRect.size);
    const snippetBase64 = snippetCanvas.toBuffer('image/png').toString('base64');
    const preview = await matchPdfPagesBySnippet({ pdfBuffer, snippetBase64, matchThreshold: 0.9 });
    await document.destroy();

    const store = new InMemoryWorkerStore({
      sources: [
        {
          id: 'source-1',
          ownerUserId: null,
          ownerGroupId: null,
          path: '/in',
          domainUsername: '',
          secretRef: '',
          printerDomainUsername: 'mapping-user@corp.local',
          printerSecretRef: 'plain:MappingPass!',
          routingProfileId: 'routing-image',
          a4PrinterId: 'printer-a4',
          thermalPrinterId: 'printer-thermal',
          includeFilenamePatterns: [],
          excludeFilenamePatterns: [],
          isActive: true
        }
      ],
      printers: [
        {
          id: 'printer-a4',
          name: 'A4 printer',
          type: 'A4',
          targetUri: 'mock://a4',
          domainUsername: '',
          secretRef: '',
          isActive: true
        },
        {
          id: 'printer-thermal',
          name: 'Thermal printer',
          type: 'THERMAL',
          targetUri: 'mock://thermal',
          domainUsername: 'printer-user@corp.local',
          secretRef: 'plain:PrinterPass!',
          isActive: true
        }
      ],
      routingProfiles: [
        {
          id: 'routing-image',
          name: 'image match',
          ownerUserId: null,
          ownerGroupId: null,
          printerDomainUsername: 'profile-user@corp.local',
          printerSecretRef: 'plain:ProfilePass!',
          defaultRouteType: 'A4',
          thermalLabelPatterns: [],
          fallbackPrinterId: null,
          samplePdfName: null,
          samplePdfBase64: null,
          snippetBase64,
          matchThreshold: 0.9,
          visualRules: []
        }
      ]
    });

    const scanner = new StaticSmbScanner({
      'source-1': [
        {
          sourceId: 'source-1',
          path: '/in/visual.pdf',
          content: pdfBuffer,
          modifiedAt: new Date('2026-04-01T10:00:00.000Z')
        }
      ]
    });

    const dispatcher = new RecordingPrinterDispatcher();
    const pipeline = new WorkerPipeline(store, scanner, new MockOcrProvider(), dispatcher);
    const summary = await pipeline.runOnce();

    expect(summary.filesProcessed).toBe(1);
    expect(dispatcher.calls).toHaveLength(1);
    expect(preview.pages[0]?.isMatch).toBe(true);
    expect(dispatcher.calls[0]?.routeType).toBe('THERMAL');
    expect(dispatcher.calls[0]?.printer.id).toBe('printer-thermal');
    expect(dispatcher.calls[0]?.printer.domainUsername).toBe('printer-user@corp.local');
    expect(dispatcher.calls[0]?.printer.secretRef).toBe('plain:PrinterPass!');
  });

  it('inherits printer credentials from mapping before routing profile defaults', async () => {
    const store = new InMemoryWorkerStore({
      sources: [
        {
          id: 'source-1',
          ownerUserId: null,
          ownerGroupId: null,
          path: '/in',
          domainUsername: '',
          secretRef: '',
          printerDomainUsername: 'mapping-user@corp.local',
          printerSecretRef: 'plain:MappingPass!',
          routingProfileId: 'routing-1',
          a4PrinterId: null,
          thermalPrinterId: 'printer-thermal',
          includeFilenamePatterns: [],
          excludeFilenamePatterns: [],
          isActive: true
        }
      ],
      printers: [
        {
          id: 'printer-thermal',
          name: 'Thermal printer',
          type: 'THERMAL',
          targetUri: 'mock://thermal',
          domainUsername: '',
          secretRef: '',
          isActive: true
        }
      ],
      routingProfiles: [
        {
          id: 'routing-1',
          name: 'profile defaults',
          ownerUserId: null,
          ownerGroupId: null,
          printerDomainUsername: 'profile-user@corp.local',
          printerSecretRef: 'plain:ProfilePass!',
          defaultRouteType: 'THERMAL',
          thermalLabelPatterns: [],
          fallbackPrinterId: null,
          samplePdfName: null,
          samplePdfBase64: null,
          snippetBase64: null,
          matchThreshold: 0.88,
          visualRules: []
        }
      ]
    });

    const scanner = new StaticSmbScanner({
      'source-1': [
        {
          sourceId: 'source-1',
          path: '/in/one.pdf',
          content: 'page',
          modifiedAt: new Date('2026-04-02T10:00:00.000Z')
        }
      ]
    });

    const dispatcher = new RecordingPrinterDispatcher();
    const pipeline = new WorkerPipeline(store, scanner, new MockOcrProvider(), dispatcher);

    await pipeline.runOnce();

    expect(dispatcher.calls).toHaveLength(1);
    expect(dispatcher.calls[0]?.printer.domainUsername).toBe('mapping-user@corp.local');
    expect(dispatcher.calls[0]?.printer.secretRef).toBe('plain:MappingPass!');
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

  it('retries a partial failure without repeating successful pages', async () => {
    const store = new InMemoryWorkerStore({
      sources: [
        {
          id: 'source-1',
          ownerUserId: 'user-1',
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
          name: 'Thermal',
          type: 'THERMAL',
          targetUri: 'socket://thermal.local:9100',
          domainUsername: '',
          secretRef: '',
          isActive: true
        }
      ],
      routingProfiles: [
        {
          id: 'routing-1',
          name: 'default',
          ownerUserId: null,
          ownerGroupId: null,
          printerDomainUsername: '',
          printerSecretRef: '',
          defaultRouteType: 'A4',
          thermalLabelPatterns: ['label'],
          fallbackPrinterId: null,
          samplePdfName: null,
          samplePdfBase64: null,
          snippetBase64: null,
          matchThreshold: 0.88,
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

    const scanner = new StaticSmbScanner({
      'source-1': [
        {
          sourceId: 'source-1',
          path: '/in/invoice-retry.pdf',
          content: 'label page\nregular page',
          modifiedAt: new Date('2026-04-02T09:00:00.000Z')
        }
      ]
    });

    const dispatchCalls: number[] = [];
    let failedOnce = false;
    const dispatcher = {
      dispatch: async (input: { page: { pageNumber: number } }) => {
        dispatchCalls.push(input.page.pageNumber);
        if (input.page.pageNumber === 2 && !failedOnce) {
          failedOnce = true;
          throw new Error('PRINTER_TIMEOUT');
        }
      }
    };

    const pipeline = new WorkerPipeline(store, scanner, new MockOcrProvider(), dispatcher);

    const firstRun = await pipeline.runOnce();
    expect(firstRun.failures).toBe(1);
    expect(firstRun.pageDispatches).toBe(1);
    expect(store.printJobs[0]?.status).toBe('FAILURE');

    const retryResult = await pipeline.retryJob(store.printJobs[0]!.id);
    expect(retryResult.summary.failures).toBe(0);
    expect(retryResult.summary.pageDispatches).toBe(1);
    expect(retryResult.summary.pageDispatchesSkipped).toBe(1);
    expect(retryResult.summary.filesProcessed).toBe(1);
    expect(retryResult.retriedJob.status).toBe('SUCCESS');
    expect(dispatchCalls).toEqual([1, 2, 2]);

    const retryPages = store.printJobPages.filter((page) => page.printJobId === retryResult.retriedJob.id);
    expect(retryPages).toEqual([
      expect.objectContaining({ pageNumber: 1, status: 'SKIPPED', errorMessage: 'ALREADY_DISPATCHED_SUCCESSFULLY' }),
      expect.objectContaining({ pageNumber: 2, status: 'SUCCESS' })
    ]);
  });

  const classificationStoreInput = (routingOverrides: Record<string, unknown> = {}) => ({
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
    masks: [],
    printers: [
      {
        id: 'printer-a4',
        name: 'Office A4',
        type: 'A4' as const,
        targetUri: 'ipp://a4.local',
        domainUsername: '',
        secretRef: '',
        isActive: true
      },
      {
        id: 'printer-thermal',
        name: 'Zebra',
        type: 'THERMAL' as const,
        targetUri: 'socket://thermal.local:9100',
        domainUsername: '',
        secretRef: '',
        isActive: true
      },
      {
        id: 'printer-thermal-returns',
        name: 'Returns Zebra',
        type: 'THERMAL' as const,
        targetUri: 'socket://returns.local:9100',
        domainUsername: '',
        secretRef: '',
        isActive: true
      }
    ],
    routingProfiles: [
      {
        id: 'routing-1',
        name: 'default',
        ownerUserId: null,
        ownerGroupId: null,
        printerDomainUsername: '',
        printerSecretRef: '',
        defaultRouteType: 'A4' as const,
        thermalLabelPatterns: [],
        fallbackPrinterId: null,
        samplePdfName: null,
        samplePdfBase64: null,
        snippetBase64: null,
        matchThreshold: 0.88,
        visualRules: [],
        ...routingOverrides
      }
    ]
  });

  const mixedDocumentContent = [
    'Faktura VAT nr 2026/07/001 suma brutto 1234,56 PLN termin płatności 14 dni',
    'DHL EXPRESS WORLDWIDE Ship to: Jan Kowalski Waybill Tracking number JJD0099887766554433',
    'UPS RETURN LABEL Ship to: Returns Center Shipper Tracking number 1Z999AA10123456784'
  ].join('\n');

  it('routes classified pages: outgoing label to thermal, return label and invoice to A4', async () => {
    const store = new InMemoryWorkerStore(classificationStoreInput());
    const scanner = new StaticSmbScanner({
      'source-1': [
        {
          sourceId: 'source-1',
          path: '/in/mixed-shipment.txt',
          content: mixedDocumentContent,
          modifiedAt: new Date('2026-07-17T08:00:00.000Z')
        }
      ]
    });
    const dispatcher = new RecordingPrinterDispatcher();
    const pipeline = new WorkerPipeline(store, scanner, new MockOcrProvider(), dispatcher);

    const summary = await pipeline.runOnce();

    expect(summary.filesProcessed).toBe(1);
    expect(dispatcher.calls.map((call) => call.routeType)).toEqual(['A4', 'THERMAL', 'A4']);
    expect(dispatcher.calls[1]?.printer.id).toBe('printer-thermal');
    expect(dispatcher.calls[1]?.page.classification?.pageClass).toBe('OUTGOING_LABEL_THERMAL');
    expect(dispatcher.calls[1]?.page.classification?.carrier).toBe('DHL');
    expect(dispatcher.calls[2]?.page.classification?.pageClass).toBe('RETURN_LABEL_A4');

    expect(store.printJobPages).toEqual([
      expect.objectContaining({ pageNumber: 1, pageClass: 'DOCUMENT_A4' }),
      expect.objectContaining({ pageNumber: 2, pageClass: 'OUTGOING_LABEL_THERMAL', carrier: 'DHL' }),
      expect.objectContaining({ pageNumber: 3, pageClass: 'RETURN_LABEL_A4', carrier: 'UPS' })
    ]);
  });

  it('honors profile classification routes including printer overrides and confidence gates', async () => {
    const store = new InMemoryWorkerStore(
      classificationStoreInput({
        classificationRoutes: [
          { pageClass: 'OUTGOING_LABEL_THERMAL', routeType: 'THERMAL', printerId: null, minConfidence: 0.99 },
          { pageClass: 'RETURN_LABEL_A4', routeType: 'THERMAL', printerId: 'printer-thermal-returns', minConfidence: 0.5 }
        ]
      })
    );
    const scanner = new StaticSmbScanner({
      'source-1': [
        {
          sourceId: 'source-1',
          path: '/in/mixed-shipment.txt',
          // Page 2 has weak label signals (carrier + one keyword ≈ 0.5 confidence),
          // page 3 is a confident UPS return label.
          content: [
            'Faktura VAT nr 2026/07/001 suma brutto 1234,56 PLN termin płatności 14 dni',
            'DPD Classic parcel service point',
            'UPS RETURN LABEL Ship to: Returns Center Shipper Tracking number 1Z999AA10123456784'
          ].join('\n'),
          modifiedAt: new Date('2026-07-17T08:00:00.000Z')
        }
      ]
    });
    const dispatcher = new RecordingPrinterDispatcher();
    const pipeline = new WorkerPipeline(store, scanner, new MockOcrProvider(), dispatcher);

    await pipeline.runOnce();

    // Outgoing label fails the 0.99 confidence gate → falls back to defaultRouteType A4.
    // Return label rule redirects to the dedicated returns thermal printer.
    expect(dispatcher.calls.map((call) => call.routeType)).toEqual(['A4', 'A4', 'THERMAL']);
    expect(dispatcher.calls[1]?.page.classification?.pageClass).toBe('OUTGOING_LABEL_THERMAL');
    expect(dispatcher.calls[2]?.printer.id).toBe('printer-thermal-returns');
  });

  it('lets explicit thermal label patterns win over classification', async () => {
    // Page 3 ("UPS RETURN LABEL …") is classified RETURN_LABEL_A4, but the profile's
    // explicit thermal pattern matches its OCR label — explicit config must win.
    const store = new InMemoryWorkerStore(classificationStoreInput({ thermalLabelPatterns: ['label'] }));
    const scanner = new StaticSmbScanner({
      'source-1': [
        {
          sourceId: 'source-1',
          path: '/in/mixed-shipment.txt',
          content: mixedDocumentContent,
          modifiedAt: new Date('2026-07-17T08:00:00.000Z')
        }
      ]
    });
    const dispatcher = new RecordingPrinterDispatcher();
    const pipeline = new WorkerPipeline(store, scanner, new MockOcrProvider(), dispatcher);

    await pipeline.runOnce();

    expect(dispatcher.calls.map((call) => call.routeType)).toEqual(['A4', 'THERMAL', 'THERMAL']);
    expect(dispatcher.calls[2]?.page.classification?.pageClass).toBe('RETURN_LABEL_A4');
  });
});
