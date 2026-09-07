import type { PdfTextItem } from '../pdf.js';

/**
 * Page classes produced by the label-detection pipeline.
 * - OUTGOING_LABEL_THERMAL: carrier shipping label meant for the parcel → thermal printer.
 * - RETURN_LABEL_A4: return label for the customer → stays on A4, scaled.
 * - DOCUMENT_A4: invoice / general info / anything else → A4.
 */
export type PageClass = 'OUTGOING_LABEL_THERMAL' | 'RETURN_LABEL_A4' | 'DOCUMENT_A4';

export type CarrierName = 'DHL' | 'UPS' | 'FEDEX' | 'DPD' | 'GLS' | 'INPOST' | 'POCZTA_POLSKA' | string;

export interface DetectedBarcode {
  symbology: string;
  value: string | null;
  boundingBox: { x: number; y: number; width: number; height: number } | null;
}

export interface PageClassification {
  pageNumber: number;
  pageClass: PageClass;
  confidence: number;
  carrier: CarrierName | null;
  isReturn: boolean;
  barcodes: DetectedBarcode[];
  evidence: string[];
  classifier: string;
}

export interface PageClassifierInput {
  pageNumber: number;
  /** Extracted text layer of the page; empty for scanned/rasterized PDFs. */
  text: string;
  textItems?: PdfTextItem[];
  pageWidth?: number;
  pageHeight?: number;
  /**
   * Single-page PDF bytes, for classifiers that rasterize (Vision Service).
   *
   * Without these the routing engine has no ink box, and without an ink box it finds no
   * labels at all: they are regions embedded on a carrier sheet, located by measurement.
   */
  pagePdf?: Buffer;
  /** Rasterized page image (PNG), when already available. */
  imagePng?: Buffer;
}

export interface PageClassifier {
  readonly name: string;
  classifyPage(input: PageClassifierInput): Promise<PageClassification>;
}

/** A whole document, for classifiers that need to see it as one. */
export interface DocumentClassifierInput {
  /** File name as scanned. Routing profiles match on it. */
  fileName: string;
  pages: PageClassifierInput[];
}

/**
 * A classifier that decides a document at a time.
 *
 * The shared routing engine works this way and cannot sensibly be driven page by page: a
 * profile is matched on the document, and a rule set can say things like "expect exactly one
 * thermal page in this document", which is a statement no single page can answer. Page-level
 * classifiers stay the simpler contract and remain the fallback.
 */
export interface DocumentPageClassifier extends PageClassifier {
  classifyDocument(input: DocumentClassifierInput): Promise<PageClassification[]>;
}

export function isDocumentClassifier(classifier: PageClassifier): classifier is DocumentPageClassifier {
  return typeof (classifier as DocumentPageClassifier).classifyDocument === 'function';
}

export interface LabelDetectionSettings {
  /** Minimum confidence to treat a page as a label at all. */
  labelConfidenceThreshold: number;
  /** Extra keywords (per deployment) that mark a page as a return label. */
  extraReturnKeywords: string[];
  /** Extra keywords that mark a page as a shipping label. */
  extraLabelKeywords: string[];
  /** Carriers to recognize in addition to the built-in set: name → keyword/regex patterns. */
  extraCarrierPatterns: Record<string, string[]>;
}

export const DEFAULT_LABEL_DETECTION_SETTINGS: LabelDetectionSettings = {
  labelConfidenceThreshold: 0.5,
  extraReturnKeywords: [],
  extraLabelKeywords: [],
  extraCarrierPatterns: {}
};
