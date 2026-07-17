import { readFile } from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { join, dirname } from 'node:path';
import { PDFDocument } from 'pdf-lib';
import { describe, expect, it } from 'vitest';
import { CupsDispatchProvider, ProviderPrinterDispatcher } from '../src/dispatch/provider-printer-dispatcher.js';
import { InMemoryWorkerStore, MockOcrProvider, StaticSmbScanner, WorkerPipeline } from '../src/pipeline.js';

const fixturePath = join(dirname(fileURLToPath(import.meta.url)), '../../../fixtures/intake/mixed-carriers.pdf');

type CapturedJob = { queue: string; options: string[]; pageCount: number | null; title: string };

/**
 * End-to-end demo: a mixed DHL/UPS/FedEx + invoice PDF dropped in an intake
 * folder is split per page, classified, and routed — outgoing labels to the
 * thermal CUPS queue, invoice/packing slip/return label to the A4 queue.
 * Only the `lp` binary is faked; everything else is the real pipeline.
 */
describe('mixed-carrier end-to-end routing', () => {
  it('classifies and routes every page of the fixture to the right CUPS queue', async () => {
    const pdf = await readFile(fixturePath);
    const captured: CapturedJob[] = [];

    const fakeExec = (async (_bin: string, args: string[]) => {
      const queue = args[args.indexOf('-d') + 1]!;
      const title = args[args.indexOf('-t') + 1]!;
      const filePath = args[args.length - 1]!;
      const submitted = await readFile(filePath);
      let pageCount: number | null = null;
      try {
        pageCount = (await PDFDocument.load(submitted)).getPageCount();
      } catch {
        pageCount = null;
      }
      const options = args.filter((_, index) => index > 0 && args[index - 1] === '-o');
      captured.push({ queue, options, pageCount, title });
      return { stdout: '', stderr: '' };
    }) as unknown as ReturnType<typeof promisify<typeof execFile>>;

    const dispatcher = new ProviderPrinterDispatcher({
      mode: 'auto',
      providers: { cups: new CupsDispatchProvider(fakeExec, 'lp') }
    });

    const store = new InMemoryWorkerStore({
      sources: [
        {
          id: 'source-intake',
          ownerUserId: null,
          ownerGroupId: null,
          path: '\\\\srv\\intake',
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
        { id: 'p-a4', name: 'Office A4', type: 'A4', targetUri: 'cups://OfficeA4', domainUsername: '', secretRef: '', isActive: true },
        { id: 'p-zebra', name: 'Zebra', type: 'THERMAL', targetUri: 'cups://Zebra-Label', domainUsername: '', secretRef: '', isActive: true }
      ]
    });

    const scanner = new StaticSmbScanner({
      'source-intake': [
        {
          sourceId: 'source-intake',
          path: '/intake/mixed-carriers.pdf',
          content: pdf,
          modifiedAt: new Date('2026-07-17T10:00:00.000Z')
        }
      ]
    });

    const pipeline = new WorkerPipeline(store, scanner, new MockOcrProvider(), dispatcher);
    const summary = await pipeline.runOnce();

    expect(summary.filesProcessed).toBe(1);
    expect(summary.pageDispatches).toBe(5);
    expect(summary.failures).toBe(0);

    // Page order: invoice, DHL label, packing slip, UPS return label, FedEx label.
    expect(captured.map((job) => job.queue)).toEqual(['OfficeA4', 'Zebra-Label', 'OfficeA4', 'OfficeA4', 'Zebra-Label']);
    // Every submission is a standalone single-page PDF.
    expect(captured.map((job) => job.pageCount)).toEqual([1, 1, 1, 1, 1]);
    // A4 submissions carry scaling options so return labels stay readable on A4.
    expect(captured[3]!.options).toContain('media=A4');
    expect(captured[3]!.options).toContain('fit-to-page');

    const pages = store.printJobPages;
    expect(pages.map((page) => page.pageClass)).toEqual([
      'DOCUMENT_A4',
      'OUTGOING_LABEL_THERMAL',
      'DOCUMENT_A4',
      'RETURN_LABEL_A4',
      'OUTGOING_LABEL_THERMAL'
    ]);
    expect(pages[1]?.carrier).toBe('DHL');
    expect(pages[3]?.carrier).toBe('UPS');
    expect(pages[4]?.carrier).toBe('FEDEX');
    expect(pages.every((page) => page.status === 'SUCCESS')).toBe(true);
  });
});
