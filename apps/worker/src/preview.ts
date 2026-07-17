import type { PageClassifier } from './classify/types.js';
import { extractPdfPages } from './pdf.js';
import { extractSinglePagePdf } from './pdf-split.js';
import {
  resolveRoute,
  type OcrPageResult,
  type RouteType,
  type WorkerClassificationRoute,
  type WorkerRoutingProfile
} from './pipeline.js';

export interface ClassificationPreviewProfile {
  defaultRouteType: RouteType;
  thermalLabelPatterns: string[];
  classificationRoutes: WorkerClassificationRoute[];
}

export interface ClassificationPreviewPage {
  pageNumber: number;
  pageClass: string;
  confidence: number;
  carrier: string | null;
  isReturn: boolean;
  evidence: string[];
  classifier: string;
  routeType: RouteType;
  decidedBy: string;
  hasTextLayer: boolean;
}

export interface ClassificationPreviewResult {
  pages: ClassificationPreviewPage[];
}

/**
 * Admin-UI preview: classifies every page of an uploaded PDF with the live
 * classifier stack (Vision Service when configured, heuristics otherwise) and
 * resolves routes with the same precedence the pipeline uses. Visual rectangle
 * rules and image-snippet matching are not simulated here — the preview covers
 * the classification/keyword/default part of the decision.
 */
export async function previewClassification(input: {
  pdfBuffer: Buffer;
  profile: ClassificationPreviewProfile;
  classifier: PageClassifier;
}): Promise<ClassificationPreviewResult> {
  const extractedPages = await extractPdfPages(input.pdfBuffer);

  const routingProfile: WorkerRoutingProfile = {
    id: 'preview',
    name: 'preview',
    ownerUserId: null,
    ownerGroupId: null,
    printerDomainUsername: '',
    printerSecretRef: '',
    defaultRouteType: input.profile.defaultRouteType,
    thermalLabelPatterns: input.profile.thermalLabelPatterns,
    fallbackPrinterId: null,
    samplePdfName: null,
    samplePdfBase64: null,
    snippetBase64: null,
    matchThreshold: 0.88,
    visualRules: [],
    classificationRoutes: input.profile.classificationRoutes
  };

  const pages: ClassificationPreviewPage[] = [];
  for (const extracted of extractedPages) {
    const hasTextLayer = extracted.text.trim().length > 0;
    const classification = await input.classifier.classifyPage({
      pageNumber: extracted.pageNumber,
      text: extracted.text,
      textItems: extracted.items,
      pageWidth: extracted.width,
      pageHeight: extracted.height,
      // Only ship page bytes when there is no text layer — that is when the
      // Vision Service needs to rasterize/OCR; keeps preview payloads small.
      pagePdf: hasTextLayer ? undefined : ((await extractSinglePagePdf(input.pdfBuffer, extracted.pageNumber)) ?? undefined)
    });

    // Thermal keyword patterns run against OCR labels at runtime; the closest
    // preview equivalent is matching them against the page text itself.
    const pageResult: OcrPageResult = {
      pageNumber: extracted.pageNumber,
      labels: hasTextLayer ? [extracted.text] : [],
      text: extracted.text,
      pageWidth: extracted.width,
      pageHeight: extracted.height,
      textItems: extracted.items,
      classification
    };

    const route = resolveRoute(pageResult, routingProfile);
    pages.push({
      pageNumber: extracted.pageNumber,
      pageClass: classification.pageClass,
      confidence: classification.confidence,
      carrier: classification.carrier,
      isReturn: classification.isReturn,
      evidence: classification.evidence,
      classifier: classification.classifier,
      routeType: route.routeType,
      decidedBy: route.decidedBy,
      hasTextLayer
    });
  }

  return { pages };
}
