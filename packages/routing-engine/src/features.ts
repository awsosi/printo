/**
 * Page feature model — the input contract of the routing engine.
 *
 * Both engine implementations (this one and `Printo.Agent.Core` in C#) consume exactly
 * these fields, and the conformance suite is expressed in them, so extraction can differ
 * per host (pdfium in the agent, the Vision Service on the server) while routing stays
 * provably identical.
 *
 * Everything is millimetres with a top-left origin. PDF points never leave the extractor:
 * rules are written by humans against a ruler, not against 1/72in units.
 */

/** Axis-aligned rectangle in millimetres, origin top-left of the page as displayed. */
export interface RectMm {
  xMm: number;
  yMm: number;
  widthMm: number;
  heightMm: number;
}

/** Bounding box of all non-white content on the page. */
export interface InkBox extends RectMm {
  /** height / width. 1.5 is a 4x6in label, ~1.23 an A4 invoice block. */
  aspect: number;
  /** Fraction of the page covered by ink, 0..1. Separates a label from a blank page. */
  coverage: number;
}

/** One decoded barcode with its position on the page. */
export interface DetectedBarcode extends RectMm {
  /** zxing-style symbology name, e.g. `Code128`, `PDF417`, `MaxiCode`, `DataMatrix`, `QRCode`. */
  symbology: string;
  value: string;
}

/** Result of a picture/template match, produced only when a rule asks for one. */
export interface TemplateMatch extends RectMm {
  template: string;
  /** Normalised cross-correlation score, 0..1. */
  score: number;
}

/**
 * OCR text recovered from one rectangle of the page.
 *
 * OCR is never run speculatively: the engine reports which rectangles a rule needs
 * (see `PageEvaluationRequest`), the host fills them in, and evaluation is repeated.
 */
export interface OcrRegion {
  /** The rectangle that was recognised, as resolved from the rule's `rect`. */
  rect: RectMm;
  /** Key the engine uses to look the region up again; see `ocrRegionKey`. */
  key: string;
  text: string;
}

/**
 * One positioned line of the embedded text layer.
 *
 * Needed by `text.withinRect` and by the admin rule editor, where an operator drags a
 * rectangle over a sample page to say "the words that decide this rule live *here*".
 */
export interface TextLine extends RectMm {
  text: string;
}

export type PageOrientation = 'portrait' | 'landscape';

/** Everything the engine knows about one page. */
export interface PageFeatures {
  /** 1-based index within the source document. */
  pageNumber: number;
  pageCount: number;
  pageWidthMm: number;
  pageHeightMm: number;
  orientation: PageOrientation;
  /** Page rotation in degrees as declared by the PDF (0/90/180/270). */
  rotation: number;
  /**
   * Embedded text layer, or `null` when the page has none. `null` and `''` mean the same
   * thing to the rules; the distinction is kept so traces can say "no text layer" rather
   * than "text did not match".
   */
  text: string | null;
  /** Positioned text layer, when the extractor captured it. */
  textLines?: TextLine[];
  /** `null` on a blank page. */
  inkBox: InkBox | null;
  barcodes: DetectedBarcode[];
  /** Populated lazily, keyed by `ocrRegionKey`. */
  ocrRegions?: OcrRegion[];
  /** Populated lazily by the host when an `image` rule asked for a template. */
  templateMatches?: TemplateMatch[];
}

/** Document-level context; page rules can key on the document as well as the page. */
export interface DocumentFeatures {
  /** File name as captured, e.g. `OneClickPrint_LWB188942373.pdf`. */
  fileName: string;
  /** Application that produced the print job, when the capture tier reports one. */
  sourceApp?: string;
  pageCount: number;
  pages: PageFeatures[];
}

/**
 * Stable key for an OCR region, so a filled region can be found on the second pass.
 *
 * Rounding is spelled out as `floor(v * 10 + 0.5)` rather than left to each language's
 * formatter: `toFixed` rounds half away from zero, Python's `format` rounds half to even,
 * and a region measured at exactly x.x5 would otherwise key differently in the agent, the
 * worker and the extractor.
 */
export function ocrRegionKey(rect: RectMm): string {
  const round = (value: number): string => (Math.floor(value * 10 + 0.5) / 10).toFixed(1);
  return `${round(rect.xMm)},${round(rect.yMm)},${round(rect.widthMm)},${round(rect.heightMm)}`;
}

/** Rectangle covering the whole page. */
export function pageRect(page: PageFeatures): RectMm {
  return { xMm: 0, yMm: 0, widthMm: page.pageWidthMm, heightMm: page.pageHeightMm };
}

/** Smallest rectangle containing every decoded barcode, or `null` when there are none. */
export function barcodeClusterRect(page: PageFeatures): RectMm | null {
  if (page.barcodes.length === 0) {
    return null;
  }

  let left = Number.POSITIVE_INFINITY;
  let top = Number.POSITIVE_INFINITY;
  let right = Number.NEGATIVE_INFINITY;
  let bottom = Number.NEGATIVE_INFINITY;

  for (const barcode of page.barcodes) {
    left = Math.min(left, barcode.xMm);
    top = Math.min(top, barcode.yMm);
    right = Math.max(right, barcode.xMm + barcode.widthMm);
    bottom = Math.max(bottom, barcode.yMm + barcode.heightMm);
  }

  return { xMm: left, yMm: top, widthMm: right - left, heightMm: bottom - top };
}

/** Grows a rectangle by `padMm` on every side, clamped to the page. */
export function padRect(rect: RectMm, padMm: number, page: PageFeatures): RectMm {
  const left = Math.max(0, rect.xMm - padMm);
  const top = Math.max(0, rect.yMm - padMm);
  const right = Math.min(page.pageWidthMm, rect.xMm + rect.widthMm + padMm);
  const bottom = Math.min(page.pageHeightMm, rect.yMm + rect.heightMm + padMm);
  return { xMm: left, yMm: top, widthMm: right - left, heightMm: bottom - top };
}

/** True when `inner` lies wholly inside `outer`, allowing `toleranceMm` of slack. */
export function rectContains(outer: RectMm, inner: RectMm, toleranceMm = 0): boolean {
  return (
    inner.xMm >= outer.xMm - toleranceMm &&
    inner.yMm >= outer.yMm - toleranceMm &&
    inner.xMm + inner.widthMm <= outer.xMm + outer.widthMm + toleranceMm &&
    inner.yMm + inner.heightMm <= outer.yMm + outer.heightMm + toleranceMm
  );
}

/** Fraction of `inner` that overlaps `outer`, 0..1. */
export function rectOverlapFraction(outer: RectMm, inner: RectMm): number {
  const left = Math.max(outer.xMm, inner.xMm);
  const top = Math.max(outer.yMm, inner.yMm);
  const right = Math.min(outer.xMm + outer.widthMm, inner.xMm + inner.widthMm);
  const bottom = Math.min(outer.yMm + outer.heightMm, inner.yMm + inner.heightMm);
  if (right <= left || bottom <= top) {
    return 0;
  }

  const area = inner.widthMm * inner.heightMm;
  return area > 0 ? ((right - left) * (bottom - top)) / area : 0;
}
