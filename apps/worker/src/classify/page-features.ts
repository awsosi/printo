import type { DetectedBarcode, InkBox, OcrRegion, PageFeatures, RectMm, TextLine } from '@printo/routing-engine';
import { ocrRegionKey } from '@printo/routing-engine';

/** What the Vision Service should measure beyond geometry and the ink box. */
export interface PageMeasurementRequest {
  pageNumber: number;
  pageCount: number;
  /** Single-page PDF. The service rasterizes it; the bytes never go anywhere else. */
  pagePdf: Buffer;
  /** The text layer the worker already extracted, passed through so both engines see one string. */
  text: string | null;
  textLines?: TextLine[];
  /** Decode barcodes on this page. Opt-in: it is the most expensive thing done to a page. */
  barcodes?: boolean;
  /** Rectangles a rule asked to have read. */
  ocrRects?: RectMm[];
}

/** Measures pages for the routing engine. */
export interface PageFeatureSource {
  readonly name: string;
  measure(request: PageMeasurementRequest): Promise<PageFeatures>;
}

/** Raised when the service is not there, or has no rasterizer. Callers fall back. */
export class FeatureSourceUnavailableError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'FeatureSourceUnavailableError';
  }
}

interface VisionRect {
  x_mm: number;
  y_mm: number;
  width_mm: number;
  height_mm: number;
}

interface VisionFeatureResponse {
  page_number?: number;
  page_width_mm?: number;
  page_height_mm?: number;
  orientation?: string;
  rotation?: number;
  text?: string | null;
  ink_box?: (VisionRect & { aspect?: number | null; coverage?: number }) | null;
  barcodes?: Array<VisionRect & { symbology?: string; value?: string }> | null;
  ocr_regions?: Array<{ rect: VisionRect; text?: string; lines?: Array<VisionRect & { text?: string }> }>;
}

function toRect(raw: VisionRect): RectMm {
  return { xMm: raw.x_mm, yMm: raw.y_mm, widthMm: raw.width_mm, heightMm: raw.height_mm };
}

/**
 * Measures pages through the Vision Service.
 *
 * The worker loads `pdfjs-dist` without `canvas`, so it has a text layer and page dimensions
 * and nothing else. The ink box is what the rules are actually written against — labels in
 * this corpus are regions embedded on a carrier sheet, located by measurement — and the Vision
 * Service already rasterizes, so it measures them and the worker stays free of native
 * dependencies. See docs/WINDOWS_CLIENT_PLAN.md section 10.3 for the alternatives and why
 * this one was taken.
 */
export class VisionFeatureSource implements PageFeatureSource {
  public readonly name = 'vision-features';
  private readonly baseUrl: string;
  private readonly timeoutMs: number;
  private readonly fetchImpl: typeof fetch;

  constructor(options: { baseUrl: string; timeoutMs?: number; fetchImpl?: typeof fetch }) {
    this.baseUrl = options.baseUrl.replace(/\/+$/, '');
    this.timeoutMs = options.timeoutMs ?? 20_000;
    this.fetchImpl = options.fetchImpl ?? fetch;
  }

  async measure(request: PageMeasurementRequest): Promise<PageFeatures> {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), this.timeoutMs);

    let response: Response;
    try {
      response = await this.fetchImpl(`${this.baseUrl}/v1/page-features`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        signal: controller.signal,
        body: JSON.stringify({
          page_number: request.pageNumber,
          page_count: request.pageCount,
          page_pdf_base64: request.pagePdf.toString('base64'),
          text: request.text,
          barcodes: request.barcodes === true,
          ocr_regions: (request.ocrRects ?? []).map((rect) => ({
            x_mm: rect.xMm,
            y_mm: rect.yMm,
            width_mm: rect.widthMm,
            height_mm: rect.heightMm
          }))
        })
      });
    } catch (error) {
      throw new FeatureSourceUnavailableError(
        `vision service unreachable: ${error instanceof Error ? error.message : 'fetch failed'}`
      );
    } finally {
      clearTimeout(timer);
    }

    if (!response.ok) {
      // 503 is the service telling us it was built without a rasterizer, which is a
      // deployment fact rather than a document problem: fall back, do not fail the job.
      throw new FeatureSourceUnavailableError(
        `vision service returned ${response.status} for page ${request.pageNumber}`
      );
    }

    const body = (await response.json()) as VisionFeatureResponse;
    return this.toFeatures(request, body);
  }

  private toFeatures(request: PageMeasurementRequest, body: VisionFeatureResponse): PageFeatures {
    const inkBox: InkBox | null =
      body.ink_box && body.ink_box.aspect != null
        ? {
            ...toRect(body.ink_box),
            aspect: body.ink_box.aspect,
            coverage: body.ink_box.coverage ?? 0
          }
        : null;

    const barcodes: DetectedBarcode[] | null =
      body.barcodes == null
        ? null
        : body.barcodes.map((barcode) => ({
            ...toRect(barcode),
            symbology: barcode.symbology ?? 'Unknown',
            value: barcode.value ?? ''
          }));

    const ocrRegions: OcrRegion[] = (body.ocr_regions ?? []).map((region) => {
      const rect = toRect(region.rect);
      return { rect, key: ocrRegionKey(rect), text: region.text ?? '' };
    });

    return {
      pageNumber: body.page_number ?? request.pageNumber,
      pageCount: request.pageCount,
      pageWidthMm: body.page_width_mm ?? 0,
      pageHeightMm: body.page_height_mm ?? 0,
      orientation: body.orientation === 'landscape' ? 'landscape' : 'portrait',
      rotation: body.rotation ?? 0,
      text: request.text,
      textLines: request.textLines,
      inkBox,
      barcodes,
      ocrRegions
    };
  }
}
