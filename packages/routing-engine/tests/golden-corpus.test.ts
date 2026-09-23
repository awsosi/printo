import { describe, expect, it } from 'vitest';
import {
  ONE_CLICK_PRINT_PROFILE,
  ROUTE_A4,
  ROUTE_SKIP,
  ROUTE_THERMAL,
  resolveCarrier,
  type EngineOptions,
  type WaybillHandling
} from '../src/index.js';
import {
  corpusAvailable,
  decide,
  loadCorpus,
  loadExpected,
  PRINTED_FEATURES_PATH,
  printedCorpusAvailable,
  stripTextLayer,
  type ExpectedPage
} from './helpers/corpus.js';

/**
 * The golden corpus: every page of the 258-document sample set, routed by the engine and
 * compared against the reviewed ground truth.
 *
 * Both text-layer modes are asserted. The anonymiser added a text layer to the FedEx and
 * UPS labels that production originals do not have, so a rule set that only passes with it
 * is failing - the stripped run is the one that reflects reality.
 */

const available = corpusAvailable();
const suite = available ? describe : describe.skip;

interface Mismatch {
  doc: string;
  pageNumber: number;
  expectedClass: string;
  expectedRoute: string;
  actualRoute: string;
  ruleId: string | null;
  firstFailure?: string;
}

function key(doc: string, pageNumber: number): string {
  return `${doc}#${pageNumber}`;
}

function summarize(mismatches: Mismatch[]): string {
  const byShape = new Map<string, number>();
  for (const mismatch of mismatches) {
    const shape = `${mismatch.expectedClass}: expected ${mismatch.expectedRoute}, got ${mismatch.actualRoute} (rule ${mismatch.ruleId ?? 'none'})`;
    byShape.set(shape, (byShape.get(shape) ?? 0) + 1);
  }

  const lines = [...byShape.entries()]
    .sort((left, right) => right[1] - left[1])
    .map(([shape, count]) => `  ${count} x ${shape}`);

  const examples = mismatches
    .slice(0, 8)
    .map(
      (mismatch) =>
        `  ${mismatch.doc} p${mismatch.pageNumber} [${mismatch.expectedClass}] ` +
        `expected ${mismatch.expectedRoute}, got ${mismatch.actualRoute}` +
        (mismatch.firstFailure ? ` — first failure: ${mismatch.firstFailure}` : '')
    );

  return `${mismatches.length} mismatches\n${lines.join('\n')}\nexamples:\n${examples.join('\n')}`;
}

/**
 * Where a page belongs under a waybill handling.
 *
 * `route` is the ground truth as reviewed. Every other handling moves the waybill copies - the
 * DHL courier sheets and the FedEx AWB copies - and nothing else, which is the whole of what
 * the policy promises.
 */
function expectedRoute(want: ExpectedPage, handling: WaybillHandling): string {
  if (handling === 'route' || !want.waybill) {
    return want.route;
  }
  return handling === 'a4' ? ROUTE_A4 : handling === 'thermal' ? ROUTE_THERMAL : ROUTE_SKIP;
}

const OVERRIDES: WaybillHandling[] = ['a4', 'thermal', 'skip'];

function runMode(stripText: boolean, handling: WaybillHandling = 'route'): Mismatch[] {
  const options: EngineOptions = { waybillHandling: handling };
  const documents = loadCorpus();
  const expected = loadExpected();
  const expectedByPage = new Map<string, ExpectedPage>(
    expected.pages.map((page) => [key(page.doc, page.pageNumber), page])
  );

  const mismatches: Mismatch[] = [];

  for (const original of documents) {
    const document = stripText ? stripTextLayer(original) : original;
    const decision = decide(ONE_CLICK_PRINT_PROFILE, document, options);

    for (const page of decision.pages) {
      const want = expectedByPage.get(key(document.fileName, page.pageNumber));
      if (!want) {
        throw new Error(`no ground truth for ${document.fileName} p${page.pageNumber}`);
      }
      const route = expectedRoute(want, handling);
      // Under an override the flag must agree with the ground truth too: a page the policy
      // claimed that is not a waybill copy is a misroute even when the route happens to match.
      const flagged = handling !== 'route' && (page.waybill === true) !== want.waybill;
      if (page.route !== route || flagged) {
        const failing = page.trace.rules.find((rule) => rule.ruleId === 'dhl-label-embedded');
        mismatches.push({
          doc: document.fileName,
          pageNumber: page.pageNumber,
          expectedClass: want.pageClass + (flagged ? ` (waybill flag ${String(page.waybill === true)})` : ''),
          expectedRoute: route,
          actualRoute: page.route,
          ruleId: page.ruleId,
          firstFailure: failing?.firstFailure
            ? `${failing.firstFailure.path} ${failing.firstFailure.detail} = ${String(failing.firstFailure.measured)}`
            : undefined
        });
      }
    }
  }

  return mismatches;
}

suite('golden corpus', () => {
  it('routes every page correctly with the embedded text layer', () => {
    const mismatches = runMode(false);
    expect(mismatches.length, summarize(mismatches)).toBe(0);
  });

  it('routes every page correctly with the text layer stripped', () => {
    const mismatches = runMode(true);
    expect(mismatches.length, summarize(mismatches)).toBe(0);
  });

  for (const handling of OVERRIDES) {
    it(`moves every waybill copy and nothing else under waybill handling '${handling}'`, () => {
      const mismatches = [...runMode(false, handling), ...runMode(true, handling)];
      expect(mismatches.length, summarize(mismatches)).toBe(0);
    });
  }

  it('finds the waybill copies the ground truth names', () => {
    // Guards the premise of the test above: if the flags went missing it would pass vacuously.
    const expected = loadExpected();
    expect(expected.pages.filter((page) => page.waybill).length).toBe(251);
  });

  it('covers the whole corpus', () => {
    const documents = loadCorpus();
    const expected = loadExpected();
    const pages = documents.reduce((total, document) => total + document.pages.length, 0);
    expect(pages).toBe(expected.pageCount);
  });

  it('never attributes a DHL label to GLS', () => {
    // Regression guard for the defect this work started from: every DHL MyDHL label
    // carries the literal `*GLS certified label*`, and the worker's `/\bgls\b/i` matched
    // it, mis-attributing 278 pages to GLS.
    const documents = loadCorpus();
    const offenders: string[] = [];

    for (const document of documents) {
      for (const page of document.pages) {
        const carrier = resolveCarrier(page);
        if (carrier.carrier === 'GLS') {
          offenders.push(`${document.fileName} p${page.pageNumber}`);
        }
      }
    }

    expect(offenders, `pages attributed to GLS: ${offenders.slice(0, 10).join(', ')}`).toEqual([]);
  });

  it('resolves DHL on pages carrying the MyDHL certified-label footer', () => {
    const documents = loadCorpus();
    const withFooter = documents
      .flatMap((document) => document.pages)
      .filter((page) => /GLS\s*certified\s*label/i.test(page.text ?? ''));

    expect(withFooter.length).toBeGreaterThan(0);
    for (const page of withFooter) {
      expect(resolveCarrier(page).carrier).toBe('DHL');
    }
  });
});

/**
 * The same 1266 pages as the virtual printer delivers them.
 *
 * The file-path runs above cannot see the print path: a page keeps its frame there, so the
 * geometry fast paths claim it before any content rule is reached. That is how a return-label
 * rule that sent 148 outgoing FedEx labels to A4 once printed passed both of them. This run has
 * no text layer and no page frame to lean on, so it is the one that proves Ctrl+P.
 *
 * Asserted strictly: every page on the right printer, and nobody asked about any of them. A
 * prompt on a page the rules should know is a regression in the product's speed even when the
 * user's answer would have been right.
 */
const printedSuite = printedCorpusAvailable() ? describe : describe.skip;

printedSuite('golden corpus, printed', () => {
  for (const handling of ['route', ...OVERRIDES] as WaybillHandling[]) {
    it(`routes every printed page correctly without asking anybody (waybills: ${handling})`, () => {
      const expected = loadExpected();
      const expectedByPage = new Map<string, ExpectedPage>(
        expected.pages.map((page) => [key(page.doc, page.pageNumber), page])
      );

      const mismatches: Mismatch[] = [];
      const prompted: string[] = [];
      let pages = 0;

      for (const document of loadCorpus(PRINTED_FEATURES_PATH)) {
        const decision = decide(ONE_CLICK_PRINT_PROFILE, document, { waybillHandling: handling });
        if (decision.fallback) {
          prompted.push(`${document.fileName}: ${decision.fallback.reason}`);
        }

        for (const page of decision.pages) {
          pages++;
          const want = expectedByPage.get(key(document.fileName, page.pageNumber));
          if (!want) {
            throw new Error(`no ground truth for ${document.fileName} p${page.pageNumber}`);
          }
          if (page.fallback) {
            prompted.push(`${document.fileName} p${page.pageNumber}: ${page.fallback.reason} via ${page.ruleId}`);
          }
          const route = expectedRoute(want, handling);
          if (page.route !== route) {
            mismatches.push({
              doc: document.fileName,
              pageNumber: page.pageNumber,
              expectedClass: want.pageClass,
              expectedRoute: route,
              actualRoute: page.route,
              ruleId: page.ruleId
            });
          }
        }
      }

      expect(pages).toBe(expected.pageCount);
      expect(mismatches.length, summarize(mismatches)).toBe(0);
      expect(prompted, prompted.slice(0, 10).join('\n')).toEqual([]);
    });
  }
});

if (!available) {
  describe('golden corpus', () => {
    it.skip('corpus not extracted; run tools/corpus/extract_features.py', () => {
      /* intentionally skipped */
    });
  });
}
