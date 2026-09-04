/**
 * Predicate evaluation.
 *
 * Every leaf predicate returns a trace carrying the value it measured, not just a boolean.
 * That is what turns a fallback from "routing failed" into "`inkAspect` was 1.48, the rule
 * wanted 1.6-2.3" — the difference between an admin who can fix a rule and one who cannot.
 */

import {
  barcodeClusterRect,
  ocrRegionKey,
  pageRect,
  rectOverlapFraction,
  type DocumentFeatures,
  type PageFeatures,
  type RectMm
} from './features.js';
import type {
  BarcodePredicate,
  CarrierPredicate,
  GeometryPredicate,
  ImagePredicate,
  OcrPredicate,
  PageIndexPredicate,
  Predicate,
  RangeMm,
  RectSpec,
  TextPredicate
} from './rules.js';
import type { CarrierResolution, OcrRequest, PredicateTrace } from './trace.js';

/**
 * A page whose own media is small enough to be label stock rather than a sheet carrying a
 * label. Covers 100x150, 100x200, 99x200 and 105x148 without naming any of them.
 */
const LABEL_STOCK_MAX_WIDTH_MM = 130;
const LABEL_STOCK_MAX_HEIGHT_MM = 260;

/** A barcode counts as "inside" a rectangle when most of it is. */
const BARCODE_CONTAINMENT = 0.6;

export interface EvaluationContext {
  page: PageFeatures;
  document: DocumentFeatures;
  carrier: CarrierResolution;
  /** Rule currently being evaluated, so OCR requests can name their origin. */
  ruleId: string;
  /** Filled by `ocr` predicates whose region the host has not supplied yet. */
  ocrRequests: OcrRequest[];
  /** Keys of OCR regions that were actually consulted. */
  ocrRectsUsed: Set<string>;
}

/** Collapses whitespace so a rule written as one phrase survives line breaks. */
export function normalizeText(value: string): string {
  return value.replace(/\s+/g, ' ').trim();
}

/** Strips whitespace entirely; OCR routinely loses the spaces in a bold header. */
function stripSpacing(value: string): string {
  return value.replace(/\s+/g, '');
}

function compile(pattern: string, caseSensitive: boolean): RegExp | null {
  try {
    return new RegExp(pattern, caseSensitive ? '' : 'i');
  } catch {
    return null;
  }
}

function inRange(value: number, range: RangeMm | undefined): boolean {
  if (!range) {
    return true;
  }
  if (range.min !== undefined && value < range.min) {
    return false;
  }
  if (range.max !== undefined && value > range.max) {
    return false;
  }
  return true;
}

function describeRange(range: RangeMm): string {
  if (range.min !== undefined && range.max !== undefined) {
    return `${range.min}..${range.max}`;
  }
  if (range.min !== undefined) {
    return `>=${range.min}`;
  }
  if (range.max !== undefined) {
    return `<=${range.max}`;
  }
  return 'any';
}

/** Resolves a rule's rectangle specification against the measured page. */
export function resolveRect(spec: RectSpec, page: PageFeatures): RectMm | null {
  if (spec === 'page') {
    return pageRect(page);
  }
  if (spec === 'inkBox') {
    if (!page.inkBox) {
      return null;
    }
    const { xMm, yMm, widthMm, heightMm } = page.inkBox;
    return { xMm, yMm, widthMm, heightMm };
  }
  if (spec === 'barcodeCluster') {
    return barcodeClusterRect(page);
  }
  if (spec.unit === 'mm') {
    return { xMm: spec.xMm, yMm: spec.yMm, widthMm: spec.widthMm, heightMm: spec.heightMm };
  }
  return {
    xMm: spec.x * page.pageWidthMm,
    yMm: spec.y * page.pageHeightMm,
    widthMm: spec.w * page.pageWidthMm,
    heightMm: spec.h * page.pageHeightMm
  };
}

function describeRect(spec: RectSpec): string {
  if (typeof spec === 'string') {
    return spec;
  }
  if (spec.unit === 'mm') {
    return `${spec.xMm},${spec.yMm} ${spec.widthMm}x${spec.heightMm}mm`;
  }
  return `${spec.x},${spec.y} ${spec.w}x${spec.h} of page`;
}

function leaf(
  kind: string,
  path: string,
  matched: boolean,
  detail: string,
  measured: string | number | boolean | null
): PredicateTrace {
  return { kind, path, matched, detail, measured };
}

/** Shortens a measured string so traces stay small enough to upload with every job. */
function clip(value: string, length = 80): string {
  const normalized = normalizeText(value);
  return normalized.length <= length ? normalized : `${normalized.slice(0, length)}…`;
}

function evaluateText(
  predicate: TextPredicate,
  path: string,
  context: EvaluationContext
): PredicateTrace {
  const { page } = context;
  if (page.text === null || page.text.length === 0) {
    return leaf('text', path, false, 'no text layer', null);
  }

  let haystack = page.text;

  if (predicate.withinRect) {
    const rect = resolveRect(predicate.withinRect, page);
    if (!rect) {
      return leaf('text', path, false, `withinRect ${describeRect(predicate.withinRect)}`, 'rect unresolved');
    }
    if (!page.textLines) {
      return leaf('text', path, false, `withinRect ${describeRect(predicate.withinRect)}`, 'positioned text unavailable');
    }
    haystack = page.textLines
      .filter((line) => rectOverlapFraction(rect, line) >= 0.5)
      .map((line) => line.text)
      .join(' ');
  }

  const normalized = normalizeText(haystack);
  const caseSensitive = predicate.caseSensitive === true;
  const subject = caseSensitive ? normalized : normalized.toLowerCase();

  if (predicate.contains !== undefined) {
    const needle = normalizeText(predicate.contains);
    const target = caseSensitive ? needle : needle.toLowerCase();
    const matched = subject.includes(target);
    return leaf('text', path, matched, `contains "${needle}"`, matched ? needle : clip(normalized));
  }

  if (predicate.matches !== undefined) {
    const regex = compile(predicate.matches, caseSensitive);
    if (!regex) {
      return leaf('text', path, false, `matches /${predicate.matches}/`, 'invalid regex');
    }
    const found = regex.exec(normalized);
    return leaf(
      'text',
      path,
      found !== null,
      `matches /${predicate.matches}/`,
      found ? clip(found[0]) : clip(normalized)
    );
  }

  return leaf('text', path, normalized.length > 0, 'any text', normalized.length);
}

function evaluateOcr(
  predicate: OcrPredicate,
  path: string,
  context: EvaluationContext
): PredicateTrace {
  const { page } = context;
  const rect = resolveRect(predicate.rect, page);
  if (!rect) {
    return leaf('ocr', path, false, `rect ${describeRect(predicate.rect)}`, 'rect unresolved');
  }

  const key = ocrRegionKey(rect);
  const regions = page.ocrRegions ?? [];
  // An exact key first; otherwise any region that already covers the requested rectangle,
  // because the text of a superset region contains the text of the subset.
  const region =
    regions.find((candidate) => candidate.key === key) ??
    regions.find((candidate) => rectOverlapFraction(candidate.rect, rect) >= 0.98);

  if (!region) {
    context.ocrRequests.push({
      pageNumber: page.pageNumber,
      rect,
      key,
      ruleId: context.ruleId,
      spec: predicate.rect
    });
    return leaf('ocr', path, false, `rect ${describeRect(predicate.rect)}`, 'ocr pending');
  }

  context.ocrRectsUsed.add(region.key);

  const caseSensitive = predicate.caseSensitive === true;
  const ignoreSpacing = predicate.ignoreSpacing !== false;
  const normalized = normalizeText(region.text);
  const variants = ignoreSpacing ? [normalized, stripSpacing(normalized)] : [normalized];

  if (predicate.contains !== undefined) {
    const needle = normalizeText(predicate.contains);
    const needles = ignoreSpacing ? [needle, stripSpacing(needle)] : [needle];
    const matched = variants.some((variant, index) => {
      const target = needles[Math.min(index, needles.length - 1)];
      return caseSensitive
        ? variant.includes(target)
        : variant.toLowerCase().includes(target.toLowerCase());
    });
    return leaf('ocr', path, matched, `contains "${needle}"`, matched ? needle : clip(normalized));
  }

  if (predicate.matches !== undefined) {
    const regex = compile(predicate.matches, caseSensitive);
    if (!regex) {
      return leaf('ocr', path, false, `matches /${predicate.matches}/`, 'invalid regex');
    }
    for (const variant of variants) {
      const found = regex.exec(variant);
      if (found) {
        return leaf('ocr', path, true, `matches /${predicate.matches}/`, clip(found[0]));
      }
    }
    return leaf('ocr', path, false, `matches /${predicate.matches}/`, clip(normalized));
  }

  return leaf('ocr', path, normalized.length > 0, 'any text', normalized.length);
}

function evaluateBarcode(
  predicate: BarcodePredicate,
  path: string,
  context: EvaluationContext
): PredicateTrace {
  const { page } = context;
  let candidates = page.barcodes;

  if (predicate.rect) {
    const rect = resolveRect(predicate.rect, page);
    if (!rect) {
      return leaf('barcode', path, false, `rect ${describeRect(predicate.rect)}`, 'rect unresolved');
    }
    candidates = candidates.filter(
      (barcode) => rectOverlapFraction(rect, barcode) >= BARCODE_CONTAINMENT
    );
  }

  if (predicate.symbology && predicate.symbology.length > 0) {
    const wanted = predicate.symbology.map((name) => name.toLowerCase());
    candidates = candidates.filter((barcode) => wanted.includes(barcode.symbology.toLowerCase()));
  }

  if (predicate.valueContains !== undefined) {
    const needle = predicate.valueContains.toLowerCase();
    candidates = candidates.filter((barcode) => barcode.value.toLowerCase().includes(needle));
  }

  if (predicate.valueMatches !== undefined) {
    const regex = compile(predicate.valueMatches, false);
    if (!regex) {
      return leaf('barcode', path, false, `valueMatches /${predicate.valueMatches}/`, 'invalid regex');
    }
    candidates = candidates.filter((barcode) => regex.test(barcode.value));
  }

  const minCount = predicate.minCount ?? 1;
  const maxCount = predicate.maxCount;
  const matched = candidates.length >= minCount && (maxCount === undefined || candidates.length <= maxCount);

  const constraint = [
    predicate.symbology ? `symbology ${predicate.symbology.join('|')}` : null,
    predicate.valueMatches ? `valueMatches /${predicate.valueMatches}/` : null,
    predicate.valueContains ? `valueContains "${predicate.valueContains}"` : null,
    `count >= ${minCount}`,
    maxCount !== undefined ? `count <= ${maxCount}` : null
  ]
    .filter((part): part is string => part !== null)
    .join(', ');

  // Report what was actually on the page, not just the filtered count: "0 matched, page had
  // 3 Code128" is diagnosable, "0" is not.
  const measured =
    candidates.length > 0
      ? `${candidates.length} matched: ${candidates.map((barcode) => `${barcode.symbology}:${clip(barcode.value, 24)}`).join(', ')}`
      : `0 matched of ${page.barcodes.length} on page` +
        (page.barcodes.length > 0
          ? ` (${page.barcodes.map((barcode) => barcode.symbology).join(', ')})`
          : '');

  return leaf('barcode', path, matched, constraint, measured);
}

function evaluateImage(
  predicate: ImagePredicate,
  path: string,
  context: EvaluationContext
): PredicateTrace {
  const { page } = context;
  const matches = page.templateMatches ?? [];
  const relevant = matches.filter((match) => match.template === predicate.template);

  if (relevant.length === 0) {
    return leaf(
      'image',
      path,
      false,
      `template ${predicate.template} >= ${predicate.threshold}`,
      matches.length === 0 ? 'no template matching performed' : 'template not matched'
    );
  }

  let best = relevant[0];
  for (const match of relevant) {
    if (match.score > best.score) {
      best = match;
    }
  }

  if (predicate.searchRect) {
    const rect = resolveRect(predicate.searchRect, page);
    if (rect && rectOverlapFraction(rect, best) < 0.5) {
      return leaf(
        'image',
        path,
        false,
        `template ${predicate.template} in ${describeRect(predicate.searchRect)}`,
        `best match outside search area (score ${best.score.toFixed(3)})`
      );
    }
  }

  return leaf(
    'image',
    path,
    best.score >= predicate.threshold,
    `template ${predicate.template} >= ${predicate.threshold}`,
    Number(best.score.toFixed(3))
  );
}

function evaluateGeometry(
  predicate: GeometryPredicate,
  path: string,
  context: EvaluationContext
): PredicateTrace {
  const { page } = context;
  const checks: Array<{ name: string; ok: boolean; measured: number | string | boolean }> = [];

  if (predicate.orientation) {
    checks.push({
      name: `orientation ${predicate.orientation}`,
      ok: page.orientation === predicate.orientation,
      measured: page.orientation
    });
  }

  const addRange = (name: string, range: RangeMm | undefined, value: number | null): void => {
    if (!range) {
      return;
    }
    if (value === null) {
      checks.push({ name: `${name} ${describeRange(range)}`, ok: false, measured: 'no ink box' });
      return;
    }
    checks.push({
      name: `${name} ${describeRange(range)}`,
      ok: inRange(value, range),
      measured: Number(value.toFixed(2))
    });
  };

  addRange('pageWidthMm', predicate.pageWidthMm, page.pageWidthMm);
  addRange('pageHeightMm', predicate.pageHeightMm, page.pageHeightMm);
  addRange('inkWidthMm', predicate.inkWidthMm, page.inkBox?.widthMm ?? null);
  addRange('inkHeightMm', predicate.inkHeightMm, page.inkBox?.heightMm ?? null);
  addRange('inkXMm', predicate.inkXMm, page.inkBox?.xMm ?? null);
  addRange('inkYMm', predicate.inkYMm, page.inkBox?.yMm ?? null);
  addRange('inkAspect', predicate.inkAspect, page.inkBox?.aspect ?? null);
  addRange('inkCoverage', predicate.inkCoverage, page.inkBox?.coverage ?? null);

  if (predicate.pageIsLabelStock !== undefined) {
    const isLabelStock =
      page.pageWidthMm <= LABEL_STOCK_MAX_WIDTH_MM &&
      page.pageHeightMm <= LABEL_STOCK_MAX_HEIGHT_MM;
    checks.push({
      name: `pageIsLabelStock ${predicate.pageIsLabelStock}`,
      ok: isLabelStock === predicate.pageIsLabelStock,
      measured: `${page.pageWidthMm}x${page.pageHeightMm}mm`
    });
  }

  const failure = checks.find((check) => !check.ok);
  if (failure) {
    return leaf('geometry', path, false, failure.name, failure.measured);
  }

  return leaf(
    'geometry',
    path,
    true,
    checks.map((check) => check.name).join(', ') || 'any geometry',
    checks.map((check) => String(check.measured)).join(', ')
  );
}

function evaluateCarrier(
  predicate: CarrierPredicate,
  path: string,
  context: EvaluationContext
): PredicateTrace {
  const { carrier } = context;
  const measured = `${carrier.carrier ?? 'none'}@${carrier.confidence.toFixed(2)}`;

  if (predicate.minConfidence !== undefined && carrier.confidence < predicate.minConfidence) {
    return leaf('carrier', path, false, `minConfidence ${predicate.minConfidence}`, measured);
  }

  if (predicate.is !== undefined) {
    return leaf('carrier', path, carrier.carrier === predicate.is, `is ${predicate.is}`, measured);
  }

  if (predicate.in !== undefined) {
    const matched = carrier.carrier !== null && predicate.in.includes(carrier.carrier);
    return leaf('carrier', path, matched, `in ${predicate.in.join('|')}`, measured);
  }

  return leaf('carrier', path, carrier.carrier !== null, 'resolved', measured);
}

function evaluatePageIndex(
  predicate: PageIndexPredicate,
  path: string,
  context: EvaluationContext
): PredicateTrace {
  const { page } = context;
  const measured = `${page.pageNumber}/${page.pageCount}`;

  if (predicate.is === 'first') {
    return leaf('pageIndex', path, page.pageNumber === 1, 'is first', measured);
  }
  if (predicate.is === 'last') {
    return leaf('pageIndex', path, page.pageNumber === page.pageCount, 'is last', measured);
  }
  if (predicate.nth !== undefined) {
    return leaf('pageIndex', path, page.pageNumber === predicate.nth, `nth ${predicate.nth}`, measured);
  }
  if (predicate.range) {
    const ok =
      (predicate.range.min === undefined || page.pageNumber >= predicate.range.min) &&
      (predicate.range.max === undefined || page.pageNumber <= predicate.range.max);
    return leaf('pageIndex', path, ok, `range ${describeRange(predicate.range)}`, measured);
  }

  return leaf('pageIndex', path, true, 'any', measured);
}

/** Evaluates one predicate, producing a full trace of what was measured. */
export function evaluatePredicate(
  predicate: Predicate,
  context: EvaluationContext,
  path = ''
): PredicateTrace {
  // `all` and `any` short-circuit. This is not just an optimisation: it is what makes
  // `all: [ geometry-guard, ocr-check ]` lazy, so OCR runs only on the handful of pages
  // whose geometry says they could be a label and whose text layer said nothing useful.
  if ('all' in predicate) {
    const children: PredicateTrace[] = [];
    for (const [index, child] of predicate.all.entries()) {
      const result = evaluatePredicate(child, context, `${path}all[${index}].`);
      children.push(result);
      if (!result.matched) {
        return { kind: 'all', path: path || 'all', matched: false, children };
      }
    }
    return { kind: 'all', path: path || 'all', matched: true, children };
  }

  if ('any' in predicate) {
    const children: PredicateTrace[] = [];
    for (const [index, child] of predicate.any.entries()) {
      const result = evaluatePredicate(child, context, `${path}any[${index}].`);
      children.push(result);
      if (result.matched) {
        return { kind: 'any', path: path || 'any', matched: true, children };
      }
    }
    return { kind: 'any', path: path || 'any', matched: false, children };
  }

  if ('not' in predicate) {
    const child = evaluatePredicate(predicate.not, context, `${path}not.`);
    return { kind: 'not', path: path || 'not', matched: !child.matched, children: [child] };
  }

  if ('text' in predicate) {
    return evaluateText(predicate.text, `${path}text`, context);
  }
  if ('ocr' in predicate) {
    return evaluateOcr(predicate.ocr, `${path}ocr`, context);
  }
  if ('barcode' in predicate) {
    return evaluateBarcode(predicate.barcode, `${path}barcode`, context);
  }
  if ('image' in predicate) {
    return evaluateImage(predicate.image, `${path}image`, context);
  }
  if ('geometry' in predicate) {
    return evaluateGeometry(predicate.geometry, `${path}geometry`, context);
  }
  if ('carrier' in predicate) {
    return evaluateCarrier(predicate.carrier, `${path}carrier`, context);
  }
  return evaluatePageIndex(predicate.pageIndex, `${path}pageIndex`, context);
}

/** Depth-first search for the first failing leaf, which is what an admin needs to see. */
export function findFirstFailure(trace: PredicateTrace): PredicateTrace | undefined {
  if (trace.matched) {
    return undefined;
  }
  if (!trace.children || trace.children.length === 0) {
    return trace;
  }
  for (const child of trace.children) {
    const failure = findFirstFailure(child);
    if (failure) {
      return failure;
    }
  }
  return trace;
}
