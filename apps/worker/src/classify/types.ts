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
  /** Single-page PDF bytes, for classifiers that rasterize/OCR (Vision Service). */
  pagePdf?: Buffer;
  /** Rasterized page image (PNG), when already available. */
  imagePng?: Buffer;
}

export interface PageClassifier {
  readonly name: string;
  classifyPage(input: PageClassifierInput): Promise<PageClassification>;
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
