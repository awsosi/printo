import { describe, expect, it } from 'vitest';
import {
  InMemoryWorkerStore,
  MockOcrProvider,
  RecordingPrinterDispatcher,
  StaticSmbScanner,
  WorkerPipeline,
  type ScannedFile
} from '../src/pipeline.js';

describe('worker pipeline', () => {
  it('processes masked files, routes pages, and skips duplicates', async () => {
    const store = new InMemoryWorkerStore({
      sources: [
        {
          id: 'source-1',
          ownerUserId: 'user-1',
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
          isActive: true
        },
        {
          id: 'printer-thermal',
          name: 'GK420d',
          type: 'THERMAL',
          targetUri: 'socket://thermal.local:9100',
          isActive: true
        }
      ],
      routingProfiles: [
        {
          id: 'routing-1',
          name: 'default',
          thermalLabelPatterns: ['label'],
          fallbackPrinterId: null
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
    expect(dispatcher.calls[1]?.routeType).toBe('A4');

    const secondRun = await pipeline.runOnce();
    expect(secondRun.filesMatched).toBe(1);
    expect(secondRun.filesProcessed).toBe(0);
    expect(secondRun.filesSkippedDedup).toBe(1);
    expect(dispatcher.calls).toHaveLength(2);
  });
});
