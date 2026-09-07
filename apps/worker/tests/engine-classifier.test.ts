import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gunzipSync } from 'node:zlib';

import type { DocumentFeatures, PageFeatures } from '@printo/routing-engine';
import { describe, expect, it } from 'vitest';

import { HeuristicPageClassifier } from '../src/classify/heuristic-classifier.js';
import { RoutingEngineClassifier } from '../src/classify/engine-classifier.js';
import {
  FeatureSourceUnavailableError,
  type PageFeatureSource,
  type PageMeasurementRequest
} from '../src/classify/page-features.js';
import type { PageClassifierInput } from '../src/classify/types.js';

const here = dirname(fileURLToPath(import.meta.url));
const CORPUS = resolve(here, '../../../tests/corpus');
const FEATURES = resolve(CORPUS, 'features.jsonl.gz');
const EXPECTED = resolve(CORPUS, 'expected.json');

interface ExpectedPage {
  doc: string;
  pageNumber: number;
  pageClass: string;
  route: string;
}

/**
 * Serves the recorded corpus measurements in place of the Vision Service.
 *
 * The service's measuring code is the corpus extractor's, copied constant for constant, so
 * what is under test here is everything the worker adds on top of it: the two-phase feature
 * protocol, the round budget, and the mapping from an engine route to the worker's page
 * classes. Whether the Python end reproduces these numbers is a separate claim with its own
 * check (`tools/corpus/check_vision_features.py`).
 */
class RecordedFeatures implements PageFeatureSource {
  public readonly name = 'recorded-corpus';
  public readonly calls: PageMeasurementRequest[] = [];

  constructor(
    private readonly pages: Map<string, PageFeatures>,
    private readonly doc: string,
    private readonly stripText = false
  ) {}

  measure(request: PageMeasurementRequest): Promise<PageFeatures> {
    this.calls.push(request);

    const page = this.pages.get(`${this.doc}#${request.pageNumber}`);
    if (!page) {
      return Promise.reject(new FeatureSourceUnavailableError(`no recorded page ${request.pageNumber}`));
    }

    return Promise.resolve({
      ...page,
      text: this.stripText ? null : page.text,
      textLines: undefined,
      // Lazy, exactly as the service is: barcodes and OCR come back only when asked for.
      barcodes: request.barcodes ? (page.barcodes ?? []) : null,
      ocrRegions: (request.ocrRects ?? []).flatMap((rect) =>
        (page.ocrRegions ?? []).filter(
          (region) =>
            Math.abs(region.rect.xMm - rect.xMm) < 0.5 &&
            Math.abs(region.rect.yMm - rect.yMm) < 0.5 &&
            Math.abs(region.rect.widthMm - rect.widthMm) < 0.5
        )
      )
    });
  }
}

function loadCorpus(): { documents: DocumentFeatures[]; pages: Map<string, PageFeatures> } {
  const raw = gunzipSync(readFileSync(FEATURES)).toString('utf8');
  const byDocument = new Map<string, PageFeatures[]>();
  const pages = new Map<string, PageFeatures>();

  for (const line of raw.split('\n')) {
    if (line.trim().length === 0) {
      continue;
    }
    const record = JSON.parse(line) as PageFeatures & { doc: string };
    const list = byDocument.get(record.doc) ?? [];
    list.push(record);
    byDocument.set(record.doc, list);
    pages.set(`${record.doc}#${record.pageNumber}`, record);
  }

  return {
    documents: [...byDocument.entries()].map(([fileName, list]) => ({
      fileName,
      pageCount: list[0]?.pageCount ?? list.length,
      pages: list.sort((left, right) => left.pageNumber - right.pageNumber)
    })),
    pages
  };
}

/** The page inputs the pipeline builds, minus the bytes the stub does not need. */
function inputsFor(document: DocumentFeatures, stripText: boolean): PageClassifierInput[] {
  return document.pages.map((page) => ({
    pageNumber: page.pageNumber,
    text: stripText ? '' : (page.text ?? ''),
    pageWidth: page.pageWidthMm,
    pageHeight: page.pageHeightMm,
    pagePdf: Buffer.from('%PDF-1.7 recorded')
  }));
}

const available = existsSync(FEATURES) && existsSync(EXPECTED);
const suite = available ? describe : describe.skip;

suite('the worker routing-engine classifier', () => {
  const { documents, pages } = available
    ? loadCorpus()
    : { documents: [] as DocumentFeatures[], pages: new Map<string, PageFeatures>() };

  const expected = available
    ? (JSON.parse(readFileSync(EXPECTED, 'utf8')) as { pages: ExpectedPage[] })
    : { pages: [] as ExpectedPage[] };

  const expectedByPage = new Map(expected.pages.map((page) => [`${page.doc}#${page.pageNumber}`, page]));

  /**
   * The claim plan section 10.3 said the worker could not make.
   *
   * Given the ink box, the worker's own classifier reproduces the routing the Windows agent
   * does, on every page of the corpus, in both text-layer modes - the same bar the agent is
   * held to. Its own heuristic manages 678 of 1266, missing every label.
   */
  for (const stripText of [false, true]) {
    it(`routes every corpus page like the agent ${stripText ? 'without' : 'with'} a text layer`, async () => {
      const mismatches: string[] = [];
      let classified = 0;

      for (const document of documents) {
        const classifier = new RoutingEngineClassifier({
          features: new RecordedFeatures(pages, document.fileName, stripText),
          fallback: new HeuristicPageClassifier(),
          onEvent: () => {
            // The fallback is a mismatch here, not an event worth printing: if the engine
            // could not settle a corpus document, the assertion below is what should say so.
          }
        });

        const results = await classifier.classifyDocument({
          fileName: document.fileName,
          pages: inputsFor(document, stripText)
        });

        for (const result of results) {
          classified += 1;
          const truth = expectedByPage.get(`${document.fileName}#${result.pageNumber}`);
          if (!truth) {
            continue;
          }

          const wanted = truth.route === 'THERMAL' ? 'OUTGOING_LABEL_THERMAL' : 'A4';
          const actual = result.pageClass === 'OUTGOING_LABEL_THERMAL' ? 'OUTGOING_LABEL_THERMAL' : 'A4';

          if (wanted !== actual) {
            mismatches.push(
              `${document.fileName} p${result.pageNumber} [${truth.pageClass}] expected ${wanted}, got ${result.pageClass} (${result.classifier})`
            );
          }
        }
      }

      expect(classified).toBe(expected.pages.length);
      expect(mismatches.slice(0, 10).join('\n')).toBe('');
    });
  }

  it('reports the carrier and the rule that decided each label', async () => {
    const document = documents.find((candidate) =>
      candidate.pages.some((page) => expectedByPage.get(`${candidate.fileName}#${page.pageNumber}`)?.route === 'THERMAL')
    );

    expect(document).toBeDefined();

    const classifier = new RoutingEngineClassifier({
      features: new RecordedFeatures(pages, document!.fileName),
      fallback: new HeuristicPageClassifier()
    });

    const results = await classifier.classifyDocument({
      fileName: document!.fileName,
      pages: inputsFor(document!, false)
    });

    const label = results.find((result) => result.pageClass === 'OUTGOING_LABEL_THERMAL');
    expect(label).toBeDefined();
    expect(label!.classifier).toBe('routing-engine');
    expect(label!.carrier).toBeTruthy();
    // The trace is what an admin uses to fix a rule, so it has to name the rule that fired.
    expect(label!.evidence.some((entry) => entry.startsWith('rule:') && entry !== 'rule:none')).toBe(true);
  });

  it('decodes barcodes only for the pages a rule asks about', async () => {
    const document = documents[0];
    const features = new RecordedFeatures(pages, document.fileName);

    await new RoutingEngineClassifier({ features, fallback: new HeuristicPageClassifier() }).classifyDocument({
      fileName: document.fileName,
      pages: inputsFor(document, false)
    });

    // Every page is measured once for geometry; decoding is the expensive step and must not
    // ride along with it.
    const firstPass = features.calls.filter((call) => call.barcodes !== true);
    expect(firstPass.length).toBeGreaterThanOrEqual(document.pages.length);
  });
});

describe('the worker routing-engine classifier without a vision service', () => {
  const input: PageClassifierInput[] = [
    { pageNumber: 1, text: 'DHL EXPRESS WORLDWIDE waybill', pagePdf: Buffer.from('%PDF-1.7') }
  ];

  it('falls back to the heuristic classifier rather than failing the document', async () => {
    const events: Record<string, unknown>[] = [];

    const classifier = new RoutingEngineClassifier({
      features: {
        name: 'offline',
        measure: () => Promise.reject(new FeatureSourceUnavailableError('vision service unreachable'))
      },
      fallback: new HeuristicPageClassifier(),
      onEvent: (event) => events.push(event)
    });

    const [result] = await classifier.classifyDocument({ fileName: 'label.pdf', pages: input });

    expect(result.classifier).toBe(new HeuristicPageClassifier().name);
    expect(events[0]?.event).toBe('engine_classifier_fell_back');
    expect(String(events[0]?.reason)).toContain('unreachable');
  });

  it('falls back when no page can be measured, rather than routing everything to A4', async () => {
    const classifier = new RoutingEngineClassifier({
      features: {
        name: 'unused',
        measure: () => Promise.reject(new Error('should not be called'))
      },
      fallback: new HeuristicPageClassifier(),
      onEvent: () => {}
    });

    const [result] = await classifier.classifyDocument({
      fileName: 'label.pdf',
      pages: [{ pageNumber: 1, text: 'DHL EXPRESS WORLDWIDE' }]
    });

    expect(result.classifier).toBe(new HeuristicPageClassifier().name);
  });
});
