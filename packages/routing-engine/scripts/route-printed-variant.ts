/**
 * Routes a printed-corpus feature file against the reviewed ground truth and says what went
 * wrong, by page class and rule.
 *
 * For measuring what a print-dialog setting does to routing: simulate the corpus printed that
 * way (`tools/corpus/simulate_print.py --dpi 203`, `--scale 0.9`, `--fit`), record its features
 * with `PrintedCorpusExport` (PRINTO_PRINTED_FEATURES_OUT), then
 *
 *     npx tsx packages/routing-engine/scripts/route-printed-variant.ts <features.jsonl.gz> [handling]
 *
 * A partially written file is fine: only whole documents are routed.
 */

import { readFileSync } from 'node:fs';
import { createGunzip } from 'node:zlib';
import { Readable } from 'node:stream';
import { resolve } from 'node:path';
import {
  evaluateDocument,
  ONE_CLICK_PRINT_PROFILE,
  WAYBILL_HANDLINGS,
  type DocumentFeatures,
  type PageFeatures,
  type WaybillHandling
} from '../src/index.js';

interface ExpectedPage {
  doc: string;
  pageNumber: number;
  pageClass: string;
  route: string;
  waybill?: boolean;
}

async function readLines(path: string): Promise<string[]> {
  const chunks: Buffer[] = [];
  const gunzip = createGunzip();
  await new Promise<void>((done) => {
    gunzip.on('data', (chunk: Buffer) => chunks.push(chunk));
    gunzip.on('end', done);
    // A file still being written ends mid-stream; keep what decompressed.
    gunzip.on('error', () => done());
    Readable.from(readFileSync(path)).pipe(gunzip);
  });
  return Buffer.concat(chunks).toString('utf8').split('\n');
}

function expectedRoute(page: ExpectedPage, handling: WaybillHandling): string {
  if (!page.waybill || handling === 'route') {
    return page.route;
  }
  return handling === 'a4' ? 'A4' : handling === 'thermal' ? 'THERMAL' : 'SKIP';
}

async function main(): Promise<void> {
  const [path, handlingArg] = process.argv.slice(2);
  if (!path) {
    throw new Error('usage: route-printed-variant.ts <features.jsonl.gz> [handling]');
  }
  const handling = (handlingArg ?? 'route') as WaybillHandling;
  if (!WAYBILL_HANDLINGS.includes(handling)) {
    throw new Error(`unknown handling ${handling}`);
  }

  const expectedFile = resolve(import.meta.dirname, '../../../tests/corpus/expected.json');
  const expected = JSON.parse(readFileSync(expectedFile, 'utf8')) as {
    pages: ExpectedPage[];
  };
  const truth = new Map(expected.pages.map((page) => [`${page.doc}#${page.pageNumber}`, page]));

  const byDocument = new Map<string, PageFeatures[]>();
  for (const line of await readLines(path)) {
    if (line.trim().length === 0) {
      continue;
    }
    let record: PageFeatures & { doc: string };
    try {
      record = JSON.parse(line) as PageFeatures & { doc: string };
    } catch {
      continue;
    }
    const pages = byDocument.get(record.doc) ?? [];
    pages.push(record);
    byDocument.set(record.doc, pages);
  }

  let pages = 0;
  let prompted = 0;
  const wrong = new Map<string, number>();
  const examples: string[] = [];
  const inkByClass = new Map<string, number[]>();

  for (const [doc, list] of byDocument) {
    if (list.length < (list[0]?.pageCount ?? 0)) {
      continue;
    }
    const document: DocumentFeatures = {
      fileName: doc,
      pageCount: list.length,
      pages: list.sort((left, right) => left.pageNumber - right.pageNumber)
    };
    const result = evaluateDocument(ONE_CLICK_PRINT_PROFILE, document, {
      waybillHandling: handling
    });
    if (result.status !== 'decided') {
      throw new Error(`${doc}: the engine asked for features the file does not record`);
    }

    for (const page of result.document.pages) {
      const want = truth.get(`${doc}#${page.pageNumber}`);
      if (!want) {
        continue;
      }
      pages++;
      const features = document.pages[page.pageNumber - 1];
      if (features?.inkBox) {
        const short = Math.min(features.inkBox.widthMm, features.inkBox.heightMm);
        const values = inkByClass.get(want.pageClass) ?? [];
        values.push(short);
        inkByClass.set(want.pageClass, values);
      }
      if (page.fallback) {
        prompted++;
      }
      const route = expectedRoute(want, handling);
      if (page.route !== route || page.fallback) {
        const key = `${want.pageClass}: expected ${route}, got ${page.route}${page.fallback ? ' (asked)' : ''} via ${page.ruleId ?? '(no rule)'}`;
        wrong.set(key, (wrong.get(key) ?? 0) + 1);
        if (examples.length < 8) {
          examples.push(`${doc} p${page.pageNumber}: ${key}`);
        }
      }
    }
  }

  const misrouted = [...wrong.values()].reduce((sum, count) => sum + count, 0);
  console.log(`${path}: ${pages} pages in ${byDocument.size} documents, handling ${handling}`);
  console.log(`  wrong or asked: ${misrouted} (asked: ${prompted})`);
  for (const [key, count] of [...wrong.entries()].sort((left, right) => right[1] - left[1])) {
    console.log(`  ${String(count).padStart(4)}  ${key}`);
  }
  for (const example of examples) {
    console.log(`        e.g. ${example}`);
  }
  for (const [pageClass, values] of [...inkByClass.entries()].sort()) {
    values.sort((left, right) => left - right);
    const at = (fraction: number): string => (values[Math.floor(fraction * (values.length - 1))] ?? 0).toFixed(1);
    console.log(
      `  ink short edge ${pageClass.padEnd(22)} n=${String(values.length).padStart(4)}  min ${at(0)}  p50 ${at(0.5)}  max ${at(1)}`
    );
  }
}

await main();
