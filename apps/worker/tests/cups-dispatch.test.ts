import { readFile } from 'node:fs/promises';
import { PDFDocument } from 'pdf-lib';
import { describe, expect, it } from 'vitest';
import { CupsDispatchProvider, parseCupsTarget, ProviderPrinterDispatcher } from '../src/dispatch/provider-printer-dispatcher.js';
import { buildZplLabel, grayBitmapToZplGraphicField, looksLikeZpl } from '../src/dispatch/zpl.js';
import { extractSinglePagePdf } from '../src/pdf-split.js';
import type { DispatchRequest, WorkerPrinter } from '../src/pipeline.js';

async function makePdf(pageCount: number): Promise<Buffer> {
  const doc = await PDFDocument.create();
  for (let index = 0; index < pageCount; index += 1) {
    const page = doc.addPage([595, 842]);
    page.drawText(`Page ${index + 1}`, { x: 50, y: 780 });
  }
  return Buffer.from(await doc.save());
}

function makePrinter(overrides: Partial<WorkerPrinter> = {}): WorkerPrinter {
  return {
    id: 'printer-1',
    name: 'Test Printer',
    type: 'A4',
    targetUri: 'cups://OfficeA4',
    domainUsername: '',
    secretRef: '',
    isActive: true,
    ...overrides
  };
}

type ExecCall = { bin: string; args: string[] };

function makeExecCapture(calls: ExecCall[]) {
  return (async (bin: string, args: string[]) => {
    calls.push({ bin, args });
    return { stdout: '', stderr: '' };
  }) as unknown as ConstructorParameters<typeof CupsDispatchProvider>[0];
}

describe('parseCupsTarget', () => {
  it('parses queue-only URIs preserving case', () => {
    expect(parseCupsTarget('cups://Zebra-Label')).toEqual({ host: '', queue: 'Zebra-Label' });
  });

  it('parses host and queue', () => {
    expect(parseCupsTarget('cups://cups.local:631/OfficeA4')).toEqual({ host: 'cups.local:631', queue: 'OfficeA4' });
  });

  it('rejects non-cups URIs and empty targets', () => {
    expect(parseCupsTarget('ipp://host/queue')).toBeNull();
    expect(parseCupsTarget('cups://')).toBeNull();
    expect(parseCupsTarget('cups://a/b/c')).toBeNull();
  });
});

describe('CupsDispatchProvider', () => {
  it('submits a single extracted PDF page to an A4 queue with scaling options', async () => {
    const calls: ExecCall[] = [];
    const provider = new CupsDispatchProvider(makeExecCapture(calls), 'lp');
    const pdf = await makePdf(3);

    const request: DispatchRequest = {
      printer: makePrinter(),
      routeType: 'A4',
      file: { sourceId: 's1', path: '/in/mixed.pdf', content: pdf, modifiedAt: null },
      page: { pageNumber: 2, labels: [] }
    };

    await provider.dispatch(request, { provider: 'cups', targetUri: 'cups://OfficeA4', timeoutMs: 5000 });

    expect(calls).toHaveLength(1);
    const { args } = calls[0]!;
    expect(args).toContain('-d');
    expect(args[args.indexOf('-d') + 1]).toBe('OfficeA4');
    expect(args.join(' ')).toContain('-o media=A4');
    expect(args.join(' ')).toContain('-o fit-to-page');

    const submittedPath = args[args.length - 1]!;
    const submitted = await readFile(submittedPath).catch(() => null);
    // Temp file is removed after dispatch; capture happened before cleanup, so re-extract to verify content shape instead.
    expect(submitted).toBeNull();
    const extracted = await extractSinglePagePdf(pdf, 2);
    const reloaded = await PDFDocument.load(extracted!);
    expect(reloaded.getPageCount()).toBe(1);
  });

  it('passes raw ZPL straight through to a thermal queue with -o raw', async () => {
    const calls: ExecCall[] = [];
    const provider = new CupsDispatchProvider(makeExecCapture(calls), 'lp');
    const zpl = buildZplLabel({
      widthDots: 812,
      heightDots: 1218,
      elements: [
        { kind: 'text', x: 20, y: 20, text: 'DHL EXPRESS' },
        { kind: 'barcode128', x: 20, y: 120, value: 'JJD0099887766' }
      ]
    });

    const request: DispatchRequest = {
      printer: makePrinter({ type: 'THERMAL', targetUri: 'cups://Zebra-Label' }),
      routeType: 'THERMAL',
      file: { sourceId: 's1', path: '/in/label.zpl', content: zpl, modifiedAt: null },
      page: { pageNumber: 1, labels: [] }
    };

    await provider.dispatch(request, { provider: 'cups', targetUri: 'cups://Zebra-Label', timeoutMs: 5000 });

    const { args } = calls[0]!;
    expect(args[args.indexOf('-d') + 1]).toBe('Zebra-Label');
    expect(args.join(' ')).toContain('-o raw');
    expect(args.join(' ')).not.toContain('fit-to-page');
  });

  it('supports remote CUPS servers and extra lp options from overrides', async () => {
    const calls: ExecCall[] = [];
    const provider = new CupsDispatchProvider(makeExecCapture(calls), 'lp');
    const pdf = await makePdf(1);

    const request: DispatchRequest = {
      printer: makePrinter({ targetUri: 'cups://cups.local:631/OfficeA4' }),
      routeType: 'A4',
      file: { sourceId: 's1', path: '/in/doc.pdf', content: pdf, modifiedAt: null },
      page: { pageNumber: 1, labels: [] }
    };

    await provider.dispatch(request, {
      provider: 'cups',
      targetUri: 'cups://cups.local:631/OfficeA4',
      timeoutMs: 5000,
      lpOptions: ['sides=two-sided-long-edge']
    });

    const { args } = calls[0]!;
    expect(args[args.indexOf('-h') + 1]).toBe('cups.local:631');
    expect(args.join(' ')).toContain('-o sides=two-sided-long-edge');
  });

  it('throws on invalid cups URIs', async () => {
    const provider = new CupsDispatchProvider(makeExecCapture([]), 'lp');
    const request: DispatchRequest = {
      printer: makePrinter(),
      routeType: 'A4',
      file: { sourceId: 's1', path: '/in/doc.pdf', content: Buffer.from('x'), modifiedAt: null },
      page: { pageNumber: 1, labels: [] }
    };

    await expect(provider.dispatch(request, { provider: 'cups', targetUri: 'cups://', timeoutMs: 1000 })).rejects.toThrow(
      'INVALID_CUPS_URI'
    );
  });
});

describe('ProviderPrinterDispatcher cups auto-detection', () => {
  it('routes cups:// targets to the cups provider in auto mode', async () => {
    const calls: DispatchRequest[] = [];
    const dispatcher = new ProviderPrinterDispatcher({
      mode: 'auto',
      providers: {
        cups: {
          async dispatch(input) {
            calls.push(input);
          }
        }
      }
    });

    await dispatcher.dispatch({
      printer: makePrinter({ targetUri: 'cups://OfficeA4' }),
      routeType: 'A4',
      file: { sourceId: 's1', path: '/in/doc.pdf', content: Buffer.from('x'), modifiedAt: null },
      page: { pageNumber: 1, labels: [] }
    });

    expect(calls).toHaveLength(1);
  });
});

describe('zpl helpers', () => {
  it('builds a label with escaped control characters', () => {
    const zpl = buildZplLabel({
      widthDots: 812,
      heightDots: 1218,
      elements: [{ kind: 'text', x: 0, y: 0, text: 'A^B~C' }]
    });

    expect(zpl.startsWith('^XA')).toBe(true);
    expect(zpl.endsWith('^XZ')).toBe(true);
    expect(zpl).toContain('^PW812');
    expect(zpl).toContain('^FDA＾B～C^FS');
  });

  it('encodes a bitmap into a ^GFA graphic field', () => {
    const gf = grayBitmapToZplGraphicField({
      width: 8,
      height: 2,
      pixels: new Uint8Array([0, 0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 0])
    });

    expect(gf).toBe('^GFA,2,2,1,F00F');
  });

  it('detects raw ZPL payloads', () => {
    expect(looksLikeZpl('^XA^FDX^FS^XZ')).toBe(true);
    expect(looksLikeZpl('  ~DGR:demo.grf')).toBe(true);
    expect(looksLikeZpl('%PDF-1.7')).toBe(false);
  });
});

describe('extractSinglePagePdf', () => {
  it('extracts the requested page', async () => {
    const pdf = await makePdf(4);
    const page3 = await extractSinglePagePdf(pdf, 3);
    const reloaded = await PDFDocument.load(page3!);
    expect(reloaded.getPageCount()).toBe(1);
  });

  it('returns null for out-of-range pages and non-PDF input', async () => {
    const pdf = await makePdf(1);
    expect(await extractSinglePagePdf(pdf, 5)).toBeNull();
    expect(await extractSinglePagePdf(Buffer.from('not a pdf'), 1)).toBeNull();
  });
});
