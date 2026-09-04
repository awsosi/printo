import { describe, expect, it } from 'vitest';
import { ONE_CLICK_PRINT_PROFILE, resolveCarrier } from '../src/index.js';
import {
  corpusAvailable,
  decide,
  loadCorpus,
  loadExpected,
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

function runMode(stripText: boolean): Mismatch[] {
  const documents = loadCorpus();
  const expected = loadExpected();
  const expectedByPage = new Map<string, ExpectedPage>(
    expected.pages.map((page) => [key(page.doc, page.pageNumber), page])
  );

  const mismatches: Mismatch[] = [];

  for (const original of documents) {
    const document = stripText ? stripTextLayer(original) : original;
    const decision = decide(ONE_CLICK_PRINT_PROFILE, document);

    for (const page of decision.pages) {
      const want = expectedByPage.get(key(document.fileName, page.pageNumber));
      if (!want) {
        throw new Error(`no ground truth for ${document.fileName} p${page.pageNumber}`);
      }
      if (page.route !== want.route) {
        const failing = page.trace.rules.find((rule) => rule.ruleId === 'dhl-label-embedded');
        mismatches.push({
          doc: document.fileName,
          pageNumber: page.pageNumber,
          expectedClass: want.pageClass,
          expectedRoute: want.route,
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

if (!available) {
  describe('golden corpus', () => {
    it.skip('corpus not extracted; run tools/corpus/extract_features.py', () => {
      /* intentionally skipped */
    });
  });
}
