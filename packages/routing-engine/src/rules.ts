/**
 * Routing rule schema — the declarative contract an admin edits and both engines execute.
 *
 * The schema is deliberately data-only: no callbacks, no embedded code. That is what lets
 * a rule set be authored in the admin UI, signed into a bundle, shipped to 20-30 agents and
 * executed identically by the C# engine on the workstation and the TypeScript engine on the
 * server. Anything a rule cannot express has to become a new predicate here first, in both
 * implementations, with a conformance fixture — never a special case in one engine.
 */

/** Inclusive numeric bound. Omitting a side leaves it unbounded. */
export interface RangeMm {
  min?: number;
  max?: number;
}

/**
 * A rectangle a predicate or transform operates on.
 *
 * `inkBox` and `barcodeCluster` are resolved per page from the extracted features, which is
 * what makes one rule work across the 25+ document shapes in the corpus: the label region is
 * found by measurement, not by a hard-coded position.
 */
export type RectSpec =
  | 'page'
  | 'inkBox'
  | 'barcodeCluster'
  | { unit: 'mm'; xMm: number; yMm: number; widthMm: number; heightMm: number }
  | { unit: 'pageFraction'; x: number; y: number; w: number; h: number };

/** Matches against the embedded PDF text layer. */
export interface TextPredicate {
  contains?: string;
  /** JavaScript-flavoured regular expression source; `.NET` accepts the same subset. */
  matches?: string;
  /** Restrict the match to text inside this rectangle. Requires positioned text. */
  withinRect?: RectSpec;
  caseSensitive?: boolean;
}

/**
 * Matches against OCR output, forcing OCR of `rect` when the host has not supplied it.
 *
 * OCR text is normalised before matching (runs of whitespace collapse to a single space)
 * because recognisers routinely drop the spaces in a bold header: the DHL sheet comes back
 * as `*WAYBILLDOC*`, so a rule must be able to write `WAYBILL\s*DOC` and mean it.
 */
export interface OcrPredicate {
  rect: RectSpec;
  contains?: string;
  matches?: string;
  caseSensitive?: boolean;
  /** Also try the match with all whitespace removed. Defaults to true. */
  ignoreSpacing?: boolean;
}

/** Matches decoded barcodes. */
export interface BarcodePredicate {
  /** zxing symbology names, e.g. `Code128`, `PDF417`, `MaxiCode`, `DataMatrix`. */
  symbology?: string[];
  valueMatches?: string;
  valueContains?: string;
  minCount?: number;
  maxCount?: number;
  /** Only count barcodes whose box lies inside this rectangle. */
  rect?: RectSpec;
}

/** Print&Share-style picture matching against a named template in the bundle. */
export interface ImagePredicate {
  template: string;
  /** Minimum normalised correlation score, 0..1. */
  threshold: number;
  searchRect?: RectSpec;
}

/** Matches measured page and ink geometry. All bounds are millimetres. */
export interface GeometryPredicate {
  orientation?: 'portrait' | 'landscape';
  pageWidthMm?: RangeMm;
  pageHeightMm?: RangeMm;
  inkWidthMm?: RangeMm;
  inkHeightMm?: RangeMm;
  inkXMm?: RangeMm;
  inkYMm?: RangeMm;
  /** Ink height / ink width. A 4x6in label is 1.5, the DHL label ~1.97. */
  inkAspect?: RangeMm;
  /** Fraction of the page covered in ink, 0..1. */
  inkCoverage?: RangeMm;
  /** True when the page itself is label stock rather than a sheet carrying a label. */
  pageIsLabelStock?: boolean;
}

/** Matches the resolved carrier. */
export interface CarrierPredicate {
  is?: string;
  in?: string[];
  /** Minimum carrier-resolution confidence, 0..1. */
  minConfidence?: number;
}

/** Matches the page position inside the document. */
export interface PageIndexPredicate {
  is?: 'first' | 'last';
  /** 1-based. */
  nth?: number;
  range?: { min?: number; max?: number };
}

/** A rule condition. Composite forms nest arbitrarily. */
export type Predicate =
  | { all: Predicate[] }
  | { any: Predicate[] }
  | { not: Predicate }
  | { text: TextPredicate }
  | { ocr: OcrPredicate }
  | { barcode: BarcodePredicate }
  | { image: ImagePredicate }
  | { geometry: GeometryPredicate }
  | { carrier: CarrierPredicate }
  | { pageIndex: PageIndexPredicate };

export type RotateSpec = 'auto' | 0 | 90 | 180 | 270;
export type FitSpec = 'contain' | 'cover' | 'actual' | 'stretch';

/**
 * How the chosen region is placed on the target media.
 *
 * `media` is a free `WxH mm` string (or a named size) rather than an enum, so 100x150,
 * 100x200 and 105x148 all work with no code change — see plan section 7.4.
 */
export interface TransformSpec {
  /** Region of the page to print. Defaults to the whole page. */
  source?: RectSpec;
  /** Grow the source region by this much on every side before fitting. */
  padMm?: number;
  rotate?: RotateSpec;
  fit?: FitSpec;
  /** `100x150mm`, `100x150`, `A4`, ... */
  media?: string;
  zoomPercent?: number;
  panXMm?: number;
  panYMm?: number;
  copies?: number;
  colorMode?: 'mono' | 'color';
  duplex?: 'simplex' | 'long-edge' | 'short-edge';
  tray?: string;
}

/** What happens when a rule matches. */
export interface RuleAction {
  /** Role (`A4`, `THERMAL`) or a named printer alias from the printer map. */
  route?: string;
  transform?: TransformSpec;
  copies?: number;
  /** Queue the page for confirmation instead of printing it. */
  hold?: boolean;
  /** Confidence this rule asserts when it matches, 0..1. Defaults to 1. */
  confidence?: number;
  /** When false, evaluation continues to later rules. Defaults to true. */
  stop?: boolean;
}

export interface PageRule {
  /** Stable identifier, referenced by traces and by the review queue. */
  id: string;
  name: string;
  when: Predicate;
  then: RuleAction;
  /** Disabled rules stay in the bundle and in the trace, but never match. */
  enabled?: boolean;
}

/** Which documents a profile applies to. */
export interface ProfileMatch {
  /** Glob against the source file or job name, e.g. `OneClickPrint_*.pdf`. */
  filenameMask?: string;
  sourceApp?: string;
  minPages?: number;
  maxPages?: number;
}

export type FallbackBehaviour = 'prompt' | 'route' | 'hold';

/** Reason a page could not be routed with confidence. See plan section 6.6. */
export type FallbackReason =
  | 'NO_THERMAL_CANDIDATE'
  | 'LOW_CONFIDENCE'
  | 'AMBIGUOUS'
  | 'UNKNOWN_CARRIER'
  | 'NO_PROFILE_MATCH'
  | 'SERVER_UNAVAILABLE'
  | 'RULE_HOLD'
  | 'CROP_IMPLAUSIBLE'
  | 'RENDER_FAILED'
  | 'DECODE_FAILED';

export interface FallbackPolicy {
  /** Route used when nothing matched and the behaviour is `route`. */
  route: string;
  /** Default behaviour for any reason not named in `byReason`. */
  onUnknown: FallbackBehaviour;
  byReason?: Partial<Record<FallbackReason, FallbackBehaviour>>;
}

/**
 * What the profile expects a matching document to contain, so the engine can tell
 * "no label found" from "this document legitimately has no label".
 */
export interface DocumentExpectations {
  thermalPagesPerDocument?: RangeMm;
}

export interface RoutingProfileRules {
  /** Human-facing profile name, e.g. `Marendo OneClickPrint`. */
  profile: string;
  /** Bundle version this rule set was published as. */
  version?: number;
  match?: ProfileMatch;
  /** Pages below this confidence escalate or fall back. Defaults to 0.75. */
  confidenceThreshold?: number;
  pageRules: PageRule[];
  fallback: FallbackPolicy;
  expectations?: DocumentExpectations;
}

export const DEFAULT_CONFIDENCE_THRESHOLD = 0.75;

/** Roles every deployment has; anything else is a printer alias. */
export const ROUTE_A4 = 'A4';
export const ROUTE_THERMAL = 'THERMAL';
