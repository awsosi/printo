/**
 * Evaluation traces.
 *
 * A verdict without a trace is unactionable: "the fallback fired again" tells an admin
 * nothing. Every predicate records the value it actually measured, so the review queue can
 * say `dhl-outgoing-label failed at barcode.valueMatches ^JD\d{18,20}$ because the decoded
 * value was "JD014600009 " with a trailing space` — and the fix is one edit.
 *
 * Traces are uploaded with the job, so they are kept small and free of page content: the
 * measured values are numbers and short strings, never whole text layers.
 */

import type { FallbackReason, RectSpec, TransformSpec } from './rules.js';
import type { RectMm } from './features.js';

/** Outcome of one leaf or composite predicate. */
export interface PredicateTrace {
  /** `text`, `ocr`, `barcode`, `image`, `geometry`, `carrier`, `pageIndex`, `all`, `any`, `not`. */
  kind: string;
  /** Position within the rule condition, e.g. `all[2].barcode`. */
  path: string;
  matched: boolean;
  /** Which sub-condition was checked, e.g. `valueMatches ^JD\d{18,20}$`. */
  detail?: string;
  /** The value that was compared, rendered short. */
  measured?: string | number | boolean | null;
  /** Nested traces for `all` / `any` / `not`. */
  children?: PredicateTrace[];
}

export interface RuleTrace {
  ruleId: string;
  ruleName: string;
  matched: boolean;
  /** Present when the rule was skipped rather than evaluated. */
  skipped?: 'disabled';
  predicate?: PredicateTrace;
  /** The first failing leaf predicate — what an admin needs to see first. */
  firstFailure?: PredicateTrace;
}

/** Evidence behind the resolved carrier. */
export interface CarrierEvidence {
  source: 'barcode' | 'text' | 'ocr' | 'geometry' | 'template';
  detail: string;
  weight: number;
}

export interface CarrierResolution {
  carrier: string | null;
  confidence: number;
  evidence: CarrierEvidence[];
  /** Every carrier that scored above zero, best first — shows near-misses. */
  scores: Array<{ carrier: string; score: number }>;
}

/** Geometry as the engine measured it, echoed into the trace for the rule editor. */
export interface GeometryTrace {
  pageWidthMm: number;
  pageHeightMm: number;
  orientation: string;
  inkBox: RectMm | null;
  inkAspect: number | null;
  inkCoverage: number | null;
}

export interface PageDecisionTrace {
  pageNumber: number;
  geometry: GeometryTrace;
  carrier: CarrierResolution;
  barcodes: Array<{ symbology: string; value: string }>;
  hasTextLayer: boolean;
  /** Rectangles OCR was actually run for. Empty when no rule needed OCR. */
  ocrRectsUsed: string[];
  rules: RuleTrace[];
}

/** What the engine decided for one page. */
export interface PageDecision {
  pageNumber: number;
  /** `A4`, `THERMAL` or a printer alias. */
  route: string;
  transform?: TransformSpec;
  copies: number;
  confidence: number;
  ruleId: string | null;
  ruleName: string | null;
  /** True when the page must be confirmed before printing. */
  hold: boolean;
  fallback?: {
    reason: FallbackReason;
    behaviour: 'prompt' | 'route' | 'hold';
    message: string;
  };
  trace: PageDecisionTrace;
}

/** A rectangle the engine needs OCR for before it can finish. */
export interface OcrRequest {
  pageNumber: number;
  rect: RectMm;
  key: string;
  /** The rule that asked, for logging. */
  ruleId: string;
  /** The rect specification as written in the rule. */
  spec: RectSpec;
}

/**
 * Result of evaluating a page.
 *
 * Feature extraction is lazy by design (plan section 6.1): a page a text rule resolves at
 * high confidence is never rasterized. Rather than make the engine async — the agent is
 * synchronous, the worker is not — evaluation returns `needs-features`, the host fills the
 * requested regions in, and evaluation is repeated. Two passes is the maximum.
 */
export type PageEvaluation =
  | { status: 'decided'; decision: PageDecision }
  | { status: 'needs-features'; ocr: OcrRequest[] };

/** Document-level result. */
export interface DocumentDecision {
  profile: string | null;
  pages: PageDecision[];
  /** Document-level fallback, e.g. no page qualified as the outgoing label. */
  fallback?: {
    reason: FallbackReason;
    behaviour: 'prompt' | 'route' | 'hold';
    message: string;
    /** Pages the engine thinks are the most likely labels, best first. */
    candidatePages: number[];
  };
}
