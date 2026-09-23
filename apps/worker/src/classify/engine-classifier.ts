import type {
  BarcodeRequest,
  DocumentFeatures,
  OcrRequest,
  PageDecision,
  PageFeatures,
  RoutingProfileRules
} from '@printo/routing-engine';
import { BUILTIN_PROFILES, evaluateDocument, matchProfile } from '@printo/routing-engine';

import type {
  DocumentClassifierInput,
  DocumentPageClassifier,
  PageClass,
  PageClassification,
  PageClassifier,
  PageClassifierInput
} from './types.js';
import { FeatureSourceUnavailableError, type PageFeatureSource } from './page-features.js';

/**
 * How many times the engine may come back asking for more measurements.
 *
 * The engine's contract is that a decision settles in a bounded number of rounds — a printed
 * label needs OCR to rule out a return label and then, only if nothing claims it, a barcode.
 * Two rounds covers the shipped rule set; the cap exists so a mistaken rule cannot turn one
 * document into an unbounded loop of renders on the server.
 */
const MAX_FEATURE_ROUNDS = 2;

/** Route names the engine produces for the two roles the worker knows about, and "not printed". */
const ROUTE_THERMAL = 'THERMAL';
const ROUTE_SKIP = 'SKIP';

export interface RoutingEngineClassifierOptions {
  features: PageFeatureSource;
  /** Used when the engine cannot settle, or when the feature source is unavailable. */
  fallback: PageClassifier;
  /** Published rule set. Defaults to the profiles compiled into the engine. */
  profiles?: RoutingProfileRules[];
  onEvent?: (event: Record<string, unknown>) => void;
}

/**
 * Classifies documents with the shared routing engine — the same rules the Windows agent runs.
 *
 * <p>Why this exists: the worker had its own heuristic classifier, and the two implementations
 * were held together only by the conformance suite rather than by shared production traffic.
 * The obstacle was never the rules but the measurements — without an ink box the engine routes
 * 678 of 1266 corpus pages and misses every label, because labels in this corpus are regions
 * embedded on a carrier sheet rather than whole pages. The Vision Service already rasterizes,
 * so it measures the ink box and the worker executes the rules.</p>
 *
 * <p>It degrades rather than fails. A vision service that is down, a rule that needs a picture
 * template the server path cannot supply, a document the rules will not settle: each falls back
 * to the heuristic classifier and says so in the log, because a routing decision that is one
 * generation older is better than a document that does not print.</p>
 */
export class RoutingEngineClassifier implements DocumentPageClassifier {
  public readonly name = 'routing-engine';
  private readonly features: PageFeatureSource;
  private readonly fallback: PageClassifier;
  private readonly profiles: RoutingProfileRules[];
  private readonly onEvent: (event: Record<string, unknown>) => void;

  constructor(options: RoutingEngineClassifierOptions) {
    this.features = options.features;
    this.fallback = options.fallback;
    this.profiles = options.profiles ?? BUILTIN_PROFILES;
    this.onEvent =
      options.onEvent ??
      ((event) => {
        // eslint-disable-next-line no-console
        console.warn(JSON.stringify({ service: 'worker', ...event }));
      });
  }

  /** A single page still goes through the document path: profiles match on the document. */
  async classifyPage(input: PageClassifierInput): Promise<PageClassification> {
    const [classification] = await this.classifyDocument({
      fileName: 'page.pdf',
      pages: [input]
    });
    return classification;
  }

  async classifyDocument(input: DocumentClassifierInput): Promise<PageClassification[]> {
    const measurable = input.pages.every((page) => page.pagePdf !== undefined);
    if (!measurable) {
      // Nothing to measure means no ink box, and the engine cannot find a label without one.
      // Saying so is better than routing every page to A4 and calling it a decision.
      return this.fallBack(input, 'no page PDFs were available to measure');
    }

    let document: DocumentFeatures;
    try {
      document = {
        fileName: input.fileName,
        pageCount: input.pages.length,
        pages: await Promise.all(input.pages.map((page) => this.measure(input, page, {})))
      };
    } catch (error) {
      if (error instanceof FeatureSourceUnavailableError) {
        return this.fallBack(input, error.message);
      }
      throw error;
    }

    const profile = matchProfile(this.profiles, document);
    if (!profile) {
      return this.fallBack(input, `no routing profile matched ${input.fileName}`);
    }

    const engineOptions = input.waybillHandling ? { waybillHandling: input.waybillHandling } : {};

    for (let round = 0; round <= MAX_FEATURE_ROUNDS; round += 1) {
      const evaluation = evaluateDocument(profile, document, engineOptions);
      if (evaluation.status === 'decided') {
        return evaluation.document.pages
          .slice()
          .sort((left, right) => left.pageNumber - right.pageNumber)
          .map((decision) => this.toClassification(decision));
      }

      if (round === MAX_FEATURE_ROUNDS) {
        break;
      }

      if (evaluation.templates.length > 0) {
        // Picture matching needs the reference images from the published bundle, which live
        // on the workstation, not here. Rather than answer with a score of zero — which would
        // silently turn "this rule could not run" into "this rule did not match" — the whole
        // document goes to the classifier that does not need them.
        return this.fallBack(input, 'a rule needs a picture template, which the server path cannot supply');
      }

      try {
        document = await this.enrich(input, document, evaluation.ocr, evaluation.barcodes);
      } catch (error) {
        if (error instanceof FeatureSourceUnavailableError) {
          return this.fallBack(input, error.message);
        }
        throw error;
      }
    }

    return this.fallBack(input, 'the rule set still wanted measurements after the round budget');
  }

  /** Re-measures the pages the engine asked about, keeping everything already known. */
  private async enrich(
    input: DocumentClassifierInput,
    document: DocumentFeatures,
    ocr: OcrRequest[],
    barcodes: BarcodeRequest[]
  ): Promise<DocumentFeatures> {
    const wanted = new Map<number, { ocrRects: OcrRequest[]; barcodes: boolean }>();

    for (const request of ocr) {
      const entry = wanted.get(request.pageNumber) ?? { ocrRects: [], barcodes: false };
      entry.ocrRects.push(request);
      wanted.set(request.pageNumber, entry);
    }

    for (const request of barcodes) {
      const entry = wanted.get(request.pageNumber) ?? { ocrRects: [], barcodes: false };
      entry.barcodes = true;
      wanted.set(request.pageNumber, entry);
    }

    const pages = await Promise.all(
      document.pages.map(async (page) => {
        const ask = wanted.get(page.pageNumber);
        if (!ask) {
          return page;
        }

        const source = input.pages.find((candidate) => candidate.pageNumber === page.pageNumber);
        if (!source) {
          return page;
        }

        const measured = await this.measure(input, source, {
          ocrRects: ask.ocrRects.map((request) => request.rect),
          barcodes: ask.barcodes
        });

        return {
          ...page,
          // Regions accumulate across rounds: a second round that asked only for barcodes must
          // not discard the text the first round recognised, or the engine asks for it again
          // and the document fails as a rule set that would not settle.
          ocrRegions: [...(page.ocrRegions ?? []), ...(measured.ocrRegions ?? [])],
          barcodes: ask.barcodes ? (measured.barcodes ?? []) : page.barcodes
        };
      })
    );

    return { ...document, pages };
  }

  private measure(
    input: DocumentClassifierInput,
    page: PageClassifierInput,
    extras: { ocrRects?: { xMm: number; yMm: number; widthMm: number; heightMm: number }[]; barcodes?: boolean }
  ): Promise<PageFeatures> {
    return this.features.measure({
      pageNumber: page.pageNumber,
      pageCount: input.pages.length,
      pagePdf: page.pagePdf!,
      text: page.text?.trim() ? page.text : null,

      // No positioned text. The worker's text items are in PDF points with a bottom-left
      // origin, and the engine's are millimetres from the top left; converting them here
      // would be inventing a measurement rather than reporting one. No shipped rule uses
      // `text.withinRect`, and one that did would be told the positions are unavailable -
      // which is the truth, and which the trace says out loud.
      ocrRects: extras.ocrRects,
      barcodes: extras.barcodes
    });
  }

  /**
   * Turns an engine decision into the worker's page classification.
   *
   * The worker's three classes are a coarser vocabulary than the engine's routes, and the one
   * distinction they make that routes do not is the return label: it prints on A4 like an
   * invoice, but a site may want to see how many it produced. The rule that matched says which
   * it is, and the rules that mean "return label" say so in their id.
   */
  private toClassification(decision: PageDecision): PageClassification {
    const isReturn = (decision.ruleId ?? '').includes('return-label');
    const pageClass: PageClass =
      decision.route.toUpperCase() === ROUTE_SKIP
        ? 'WAYBILL_EXCLUDED'
        : decision.route.toUpperCase() === ROUTE_THERMAL
          ? 'OUTGOING_LABEL_THERMAL'
          : isReturn
            ? 'RETURN_LABEL_A4'
            : 'DOCUMENT_A4';

    const evidence = [
      decision.ruleId ? `rule:${decision.ruleId}` : 'rule:none',
      `route:${decision.route}`,
      ...(decision.waybill ? ['waybill'] : []),
      ...(decision.trace.carrier.carrier ? [`carrier:${decision.trace.carrier.carrier.toLowerCase()}`] : []),
      ...(decision.trace.hasTextLayer ? ['text-layer'] : ['no-text-layer']),
      ...(decision.trace.ocrRectsUsed.length > 0 ? [`ocr:${decision.trace.ocrRectsUsed.length}`] : []),
      ...(decision.trace.barcodes.length > 0 ? [`barcodes:${decision.trace.barcodes.length}`] : []),
      ...(decision.fallback ? [`fallback:${decision.fallback.reason}`] : [])
    ];

    return {
      pageNumber: decision.pageNumber,
      pageClass,
      // A page the engine held, or raised a fallback for, is reported at its own confidence:
      // the routing table has a minimum, and an uncertain page should fall below it rather than
      // be presented as a verdict.
      confidence: decision.hold ? Math.min(decision.confidence, 0.4) : decision.confidence,
      carrier: decision.trace.carrier.carrier ?? null,
      isReturn,
      barcodes: decision.trace.barcodes.map((barcode) => ({
        symbology: barcode.symbology,
        value: barcode.value,
        boundingBox: null
      })),
      evidence,
      classifier: this.name
    };
  }

  private async fallBack(input: DocumentClassifierInput, reason: string): Promise<PageClassification[]> {
    this.onEvent({
      event: 'engine_classifier_fell_back',
      fileName: input.fileName,
      classifier: this.fallback.name,
      reason
    });

    return Promise.all(input.pages.map((page) => this.fallback.classifyPage(page)));
  }
}
