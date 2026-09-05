#!/usr/bin/env tsx
/**
 * Derives conformance fixtures from the real corpus.
 *
 * The hand-written fixtures pin the cases a human thought of. These pin the ones the
 * documents actually contain: a deterministic sample of every page class, in both text-layer
 * modes, with the decision the TypeScript engine produces. The C# engine then has to
 * reproduce them exactly.
 *
 * The sample is only written when every selected page already agrees with the reviewed ground
 * truth, so a fixture can never bake in a routing bug as "expected".
 *
 * Usage: npm run fixtures:corpus -w @printo/routing-engine
 */

import { writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  evaluateDocument,
  ONE_CLICK_PRINT_PROFILE,
  type ConformanceFixture,
  type DocumentFeatures,
  type PageFeatures
} from '../src/index.js';
import {
  loadCorpus,
  loadExpected,
  stripTextLayer,
  type ExpectedPage
} from '../tests/helpers/corpus.js';

const here = dirname(fileURLToPath(import.meta.url));
const outputPath = resolve(here, '../../../tests/conformance/corpus-sample.json');

/** Pages kept per class, per text-layer mode. Enough to cover the variants, small enough to read. */
const PER_CLASS = 3;

/** Strips the parts of a page that only make the fixture bigger. */
function slim(page: PageFeatures): PageFeatures {
  return {
    ...page,
    // Per-line OCR boxes matter to the rule editor, not to routing; the region text is what
    // the predicates read.
    ocrRegions: page.ocrRegions?.map((region) => ({ ...region, lines: [] })),
    textLines: undefined
  };
}

function singlePageDocument(fileName: string, page: PageFeatures): DocumentFeatures {
  const only: PageFeatures = { ...slim(page), pageNumber: 1, pageCount: 1 };
  return { fileName, pageCount: 1, pages: [only] };
}

const documents = loadCorpus();
const expected = loadExpected();
const truth = new Map<string, ExpectedPage>(
  expected.pages.map((entry) => [`${entry.doc}#${entry.pageNumber}`, entry])
);

interface Candidate {
  doc: string;
  page: PageFeatures;
  want: ExpectedPage;
}

const byClass = new Map<string, Candidate[]>();
for (const document of documents) {
  for (const page of document.pages) {
    const want = truth.get(`${document.fileName}#${page.pageNumber}`);
    if (!want) {
      throw new Error(`no ground truth for ${document.fileName} p${page.pageNumber}`);
    }
    const bucket = byClass.get(want.pageClass) ?? [];
    bucket.push({ doc: document.fileName, page, want });
    byClass.set(want.pageClass, bucket);
  }
}

const fixtures: ConformanceFixture[] = [];
const problems: string[] = [];

for (const pageClass of [...byClass.keys()].sort()) {
  const candidates = byClass.get(pageClass) ?? [];

  // Deterministic spread across the class rather than the first N, which would all come from
  // the same document and miss the template variants.
  const step = Math.max(1, Math.floor(candidates.length / PER_CLASS));
  const picked: Candidate[] = [];
  for (let index = 0; index < candidates.length && picked.length < PER_CLASS; index += step) {
    picked.push(candidates[index]);
  }

  for (const candidate of picked) {
    for (const stripped of [false, true]) {
      const original = singlePageDocument(candidate.doc, candidate.page);
      const document = stripped ? stripTextLayer(original) : original;
      const evaluation = evaluateDocument(ONE_CLICK_PRINT_PROFILE, document);

      if (evaluation.status !== 'decided') {
        problems.push(
          `${candidate.doc} p${candidate.page.pageNumber} (${pageClass}, ` +
            `${stripped ? 'stripped' : 'text'}): engine wanted OCR the corpus does not hold`
        );
        continue;
      }

      const decision = evaluation.document.pages[0];
      if (decision.route !== candidate.want.route) {
        problems.push(
          `${candidate.doc} p${candidate.page.pageNumber} (${pageClass}, ` +
            `${stripped ? 'stripped' : 'text'}): expected ${candidate.want.route}, ` +
            `engine says ${decision.route} via ${decision.ruleId ?? 'no rule'}`
        );
        continue;
      }

      fixtures.push({
        name:
          `${pageClass} — ${candidate.doc} p${candidate.page.pageNumber}` +
          (stripped ? ' (text layer stripped)' : ''),
        rationale: candidate.want.evidence.join('; '),
        profile: 'builtin:OneClickPrint',
        document,
        expect: {
          pages: [
            {
              pageNumber: 1,
              route: decision.route,
              ruleId: decision.ruleId,
              confidence: decision.confidence,
              hold: decision.hold,
              fallbackReason: decision.fallback?.reason ?? null,
              carrier: decision.trace.carrier.carrier,
              ocrRectsUsed: [...decision.trace.ocrRectsUsed].sort()
            }
          ]
        }
      });
    }
  }
}

if (problems.length > 0) {
  console.error(`refusing to write fixtures; ${problems.length} pages disagree with ground truth:`);
  for (const problem of problems.slice(0, 20)) {
    console.error(`  ${problem}`);
  }
  process.exit(1);
}

writeFileSync(outputPath, `${JSON.stringify({ version: 1, fixtures }, null, 2)}\n`, 'utf8');
console.log(`wrote ${outputPath} — ${fixtures.length} fixtures across ${byClass.size} page classes`);
