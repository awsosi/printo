/**
 * The routing engine.
 *
 * One declarative rule set, evaluated identically here and in `Printo.Agent.Core`. The
 * engine is deliberately synchronous and side-effect free: it measures, it decides, it
 * explains. Anything that needs I/O — rasterizing, OCR, template matching — is requested
 * from the host through `PageEvaluation.needs-features` and evaluation is repeated.
 */

import { resolveCarrier, type CarrierSignatureSet } from './carrier.js';
import type { DocumentFeatures, PageFeatures, RectMm } from './features.js';
import { padRect } from './features.js';
import { evaluatePredicate, findFirstFailure, resolveRect, type EvaluationContext } from './predicates.js';
import {
  DEFAULT_CONFIDENCE_THRESHOLD,
  ROUTE_THERMAL,
  type FallbackBehaviour,
  type FallbackReason,
  type PageRule,
  type RoutingProfileRules,
  type TransformSpec
} from './rules.js';
import type {
  DocumentDecision,
  OcrRequest,
  PageDecision,
  PageEvaluation,
  RuleTrace
} from './trace.js';

export interface EngineOptions {
  /** Overrides the built-in carrier signatures; supplied by the rule bundle. */
  carrierSignatures?: CarrierSignatureSet[];
}

/** Confidence assigned to a page that no rule claimed and that took the profile default. */
const DEFAULT_ROUTE_CONFIDENCE = 0.6;

/**
 * Bounds a cropped label region must satisfy to be printable. A crop outside these is a
 * measurement failure, not a label: printing it would waste stock and hide the real problem.
 */
const CROP_MIN_SIZE_MM = 20;
const CROP_MAX_ASPECT = 4;
const CROP_MIN_ASPECT = 0.25;
const CROP_PAGE_TOLERANCE_MM = 1;

function behaviourFor(profile: RoutingProfileRules, reason: FallbackReason): FallbackBehaviour {
  return profile.fallback.byReason?.[reason] ?? profile.fallback.onUnknown;
}

/** Converts a filename glob (`OneClickPrint_*.pdf`) into an anchored regex. */
export function globToRegExp(glob: string): RegExp {
  const escaped = glob.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.');
  return new RegExp(`^${escaped}$`, 'i');
}

/** First profile whose `match` accepts the document, or `null`. */
export function matchProfile(
  profiles: RoutingProfileRules[],
  document: DocumentFeatures
): RoutingProfileRules | null {
  for (const profile of profiles) {
    const match = profile.match;
    if (!match) {
      return profile;
    }
    if (match.filenameMask && !globToRegExp(match.filenameMask).test(document.fileName)) {
      continue;
    }
    if (match.sourceApp && match.sourceApp !== '*' && match.sourceApp !== document.sourceApp) {
      continue;
    }
    if (match.minPages !== undefined && document.pageCount < match.minPages) {
      continue;
    }
    if (match.maxPages !== undefined && document.pageCount > match.maxPages) {
      continue;
    }
    return profile;
  }
  return null;
}

/** Resolves the region a transform will print, so it can be validated and rendered. */
export function resolveTransformSource(
  transform: TransformSpec | undefined,
  page: PageFeatures
): RectMm | null {
  const spec = transform?.source ?? 'page';
  const rect = resolveRect(spec, page);
  if (!rect) {
    return null;
  }
  return transform?.padMm ? padRect(rect, transform.padMm, page) : rect;
}

function cropIsPlausible(rect: RectMm | null, page: PageFeatures): string | null {
  if (!rect) {
    return 'source region could not be resolved';
  }
  if (rect.widthMm < CROP_MIN_SIZE_MM || rect.heightMm < CROP_MIN_SIZE_MM) {
    return `source region ${rect.widthMm.toFixed(1)}x${rect.heightMm.toFixed(1)}mm is smaller than ${CROP_MIN_SIZE_MM}mm`;
  }
  const aspect = rect.heightMm / rect.widthMm;
  if (aspect > CROP_MAX_ASPECT || aspect < CROP_MIN_ASPECT) {
    return `source aspect ${aspect.toFixed(2)} outside ${CROP_MIN_ASPECT}..${CROP_MAX_ASPECT}`;
  }
  if (
    rect.xMm < -CROP_PAGE_TOLERANCE_MM ||
    rect.yMm < -CROP_PAGE_TOLERANCE_MM ||
    rect.xMm + rect.widthMm > page.pageWidthMm + CROP_PAGE_TOLERANCE_MM ||
    rect.yMm + rect.heightMm > page.pageHeightMm + CROP_PAGE_TOLERANCE_MM
  ) {
    return 'source region lies outside the page';
  }
  return null;
}

/**
 * Evaluates one page.
 *
 * Rules are tried in order and the first match wins unless it sets `stop: false`. As soon
 * as a rule needs OCR that the host has not provided, evaluation stops and reports the
 * request — that laziness is what keeps a text-resolved page from ever being rasterized.
 */
export function evaluatePage(
  profile: RoutingProfileRules,
  page: PageFeatures,
  document: DocumentFeatures,
  options: EngineOptions = {}
): PageEvaluation {
  const carrier = resolveCarrier(page, options.carrierSignatures);
  const context: EvaluationContext = {
    page,
    document,
    carrier,
    ruleId: '',
    ocrRequests: [],
    ocrRectsUsed: new Set<string>()
  };

  const ruleTraces: RuleTrace[] = [];
  let winner: PageRule | null = null;

  for (const rule of profile.pageRules) {
    if (rule.enabled === false) {
      ruleTraces.push({ ruleId: rule.id, ruleName: rule.name, matched: false, skipped: 'disabled' });
      continue;
    }

    context.ruleId = rule.id;
    const predicate = evaluatePredicate(rule.when, context);

    if (context.ocrRequests.length > 0) {
      return { status: 'needs-features', ocr: dedupeOcr(context.ocrRequests) };
    }

    ruleTraces.push({
      ruleId: rule.id,
      ruleName: rule.name,
      matched: predicate.matched,
      predicate,
      firstFailure: findFirstFailure(predicate)
    });

    if (predicate.matched) {
      winner = rule;
      if (rule.then.stop !== false) {
        break;
      }
    }
  }

  const threshold = profile.confidenceThreshold ?? DEFAULT_CONFIDENCE_THRESHOLD;
  const trace: PageDecision['trace'] = {
    pageNumber: page.pageNumber,
    geometry: {
      pageWidthMm: page.pageWidthMm,
      pageHeightMm: page.pageHeightMm,
      orientation: page.orientation,
      inkBox: page.inkBox
        ? {
            xMm: page.inkBox.xMm,
            yMm: page.inkBox.yMm,
            widthMm: page.inkBox.widthMm,
            heightMm: page.inkBox.heightMm
          }
        : null,
      inkAspect: page.inkBox?.aspect ?? null,
      inkCoverage: page.inkBox?.coverage ?? null
    },
    carrier,
    barcodes: page.barcodes.map((barcode) => ({
      symbology: barcode.symbology,
      value: barcode.value
    })),
    hasTextLayer: page.text !== null && page.text.length > 0,
    ocrRectsUsed: [...context.ocrRectsUsed],
    rules: ruleTraces
  };

  if (!winner) {
    const behaviour = behaviourFor(profile, 'NO_PROFILE_MATCH');
    const decision: PageDecision = {
      pageNumber: page.pageNumber,
      route: profile.fallback.route,
      copies: 1,
      confidence: DEFAULT_ROUTE_CONFIDENCE,
      ruleId: null,
      ruleName: null,
      hold: behaviour === 'hold',
      trace
    };
    if (behaviour !== 'route') {
      decision.fallback = {
        reason: 'NO_PROFILE_MATCH',
        behaviour,
        message: 'No page rule matched; profile default applied'
      };
    }
    return { status: 'decided', decision };
  }

  const confidence = winner.then.confidence ?? 1;
  const route = winner.then.route ?? profile.fallback.route;
  const decision: PageDecision = {
    pageNumber: page.pageNumber,
    route,
    transform: winner.then.transform,
    copies: winner.then.copies ?? winner.then.transform?.copies ?? 1,
    confidence,
    ruleId: winner.id,
    ruleName: winner.name,
    hold: winner.then.hold === true,
    trace
  };

  if (winner.then.hold === true) {
    decision.fallback = {
      reason: 'RULE_HOLD',
      behaviour: behaviourFor(profile, 'RULE_HOLD'),
      message: `Rule ${winner.id} asked for confirmation`
    };
    return { status: 'decided', decision };
  }

  // A crop that cannot be printed must surface as a fallback, never as a bad print.
  if (winner.then.transform) {
    const source = resolveTransformSource(winner.then.transform, page);
    const problem = cropIsPlausible(source, page);
    if (problem) {
      const behaviour = behaviourFor(profile, 'CROP_IMPLAUSIBLE');
      decision.hold = behaviour === 'hold';
      decision.fallback = { reason: 'CROP_IMPLAUSIBLE', behaviour, message: problem };
      return { status: 'decided', decision };
    }
  }

  if (confidence < threshold) {
    const behaviour = behaviourFor(profile, 'LOW_CONFIDENCE');
    decision.hold = behaviour === 'hold';
    decision.fallback = {
      reason: 'LOW_CONFIDENCE',
      behaviour,
      message: `Rule ${winner.id} matched at ${confidence.toFixed(2)}, below threshold ${threshold.toFixed(2)}`
    };
  }

  return { status: 'decided', decision };
}

function dedupeOcr(requests: OcrRequest[]): OcrRequest[] {
  const seen = new Set<string>();
  const unique: OcrRequest[] = [];
  for (const request of requests) {
    const key = `${request.pageNumber}:${request.key}`;
    if (!seen.has(key)) {
      seen.add(key);
      unique.push(request);
    }
  }
  return unique;
}

/**
 * Evaluates a whole document, applying the document-level expectations that turn
 * "no page qualified" into an explicit, actionable fallback instead of a silent A4 job.
 */
export function evaluateDocument(
  profile: RoutingProfileRules,
  document: DocumentFeatures,
  options: EngineOptions = {}
):
  | { status: 'decided'; document: DocumentDecision }
  | { status: 'needs-features'; ocr: OcrRequest[] } {
  const decisions: PageDecision[] = [];
  const pending: OcrRequest[] = [];

  for (const page of document.pages) {
    const evaluation = evaluatePage(profile, page, document, options);
    if (evaluation.status === 'needs-features') {
      pending.push(...evaluation.ocr);
      continue;
    }
    decisions.push(evaluation.decision);
  }

  if (pending.length > 0) {
    return { status: 'needs-features', ocr: dedupeOcr(pending) };
  }

  const result: DocumentDecision = { profile: profile.profile, pages: decisions };
  const expectation = profile.expectations?.thermalPagesPerDocument;

  if (expectation) {
    const thermal = decisions.filter((decision) => decision.route === ROUTE_THERMAL);
    if (expectation.min !== undefined && thermal.length < expectation.min) {
      result.fallback = {
        reason: 'NO_THERMAL_CANDIDATE',
        behaviour: behaviourFor(profile, 'NO_THERMAL_CANDIDATE'),
        message: `Expected at least ${expectation.min} thermal page(s), found ${thermal.length}`,
        candidatePages: rankLabelCandidates(document)
      };
    } else if (expectation.max !== undefined && thermal.length > expectation.max) {
      result.fallback = {
        reason: 'AMBIGUOUS',
        behaviour: behaviourFor(profile, 'AMBIGUOUS'),
        message: `Expected at most ${expectation.max} thermal page(s), found ${thermal.length}`,
        candidatePages: thermal.map((decision) => decision.pageNumber)
      };
    }
  }

  return { status: 'decided', document: result };
}

/**
 * Pages most likely to be the label, best first.
 *
 * Used to pre-select entries in the fallback picker: in the common near-miss case the user
 * should only have to press Enter.
 */
export function rankLabelCandidates(document: DocumentFeatures): number[] {
  const scored = document.pages
    .map((page) => ({ pageNumber: page.pageNumber, score: labelLikeness(page) }))
    .filter((entry) => entry.score > 0)
    .sort((left, right) => right.score - left.score);
  return scored.map((entry) => entry.pageNumber);
}

/**
 * Carrier-agnostic "does this look like a shipping label" score.
 *
 * Deliberately generic (plan section 6.3): an unknown carrier whose ink box is label-shaped
 * still gets recognised, so a new carrier works on day one and earns a template later.
 */
export function labelLikeness(page: PageFeatures): number {
  const box = page.inkBox;
  if (!box) {
    return 0;
  }

  let score = 0;
  if (box.widthMm >= 70 && box.widthMm <= 120) {
    score += 0.4;
  }
  if (box.aspect >= 1.3 && box.aspect <= 2.4) {
    score += 0.4;
  }
  if (page.pageWidthMm <= 130 && page.pageHeightMm <= 260) {
    score += 0.2;
  }
  if (page.barcodes.length > 0) {
    score += 0.2;
  }
  return Math.min(1, score);
}
