import type { DetectedBarcode, PageClass, PageClassification, PageClassifier, PageClassifierInput } from './types.js';

export interface VisionServiceClassifierOptions {
  baseUrl: string;
  timeoutMs?: number;
  fetchImpl?: typeof fetch;
}

interface VisionClassifyResponse {
  page_class?: string;
  confidence?: number;
  carrier?: string | null;
  is_return?: boolean;
  barcodes?: Array<{
    symbology?: string;
    value?: string | null;
    bounding_box?: { x?: number; y?: number; width?: number; height?: number } | null;
  }>;
  evidence?: string[];
}

const PAGE_CLASSES: PageClass[] = ['OUTGOING_LABEL_THERMAL', 'RETURN_LABEL_A4', 'DOCUMENT_A4'];

function parsePageClass(raw: string | undefined): PageClass {
  return PAGE_CLASSES.includes(raw as PageClass) ? (raw as PageClass) : 'DOCUMENT_A4';
}

function parseBarcodes(raw: VisionClassifyResponse['barcodes']): DetectedBarcode[] {
  if (!Array.isArray(raw)) {
    return [];
  }
  return raw.map((item) => ({
    symbology: typeof item.symbology === 'string' ? item.symbology : 'UNKNOWN',
    value: typeof item.value === 'string' ? item.value : null,
    boundingBox:
      item.bounding_box && typeof item.bounding_box === 'object'
        ? {
            x: Number(item.bounding_box.x ?? 0),
            y: Number(item.bounding_box.y ?? 0),
            width: Number(item.bounding_box.width ?? 0),
            height: Number(item.bounding_box.height ?? 0)
          }
        : null
  }));
}

/**
 * HTTP client for the Vision Service (`services/vision`). Sends the page's text layer
 * plus the single-page PDF (or PNG) and receives a structured classification.
 * Contract: docs/VISION_SERVICE.md.
 */
export class VisionServiceClassifier implements PageClassifier {
  public readonly name = 'vision-service';
  private readonly baseUrl: string;
  private readonly timeoutMs: number;
  private readonly fetchImpl: typeof fetch;

  constructor(options: VisionServiceClassifierOptions) {
    this.baseUrl = options.baseUrl.replace(/\/+$/, '');
    this.timeoutMs = options.timeoutMs ?? 10_000;
    this.fetchImpl = options.fetchImpl ?? fetch;
  }

  async isHealthy(): Promise<boolean> {
    try {
      const response = await this.fetchImpl(`${this.baseUrl}/health`, {
        signal: AbortSignal.timeout(Math.min(this.timeoutMs, 3_000))
      });
      return response.ok;
    } catch {
      return false;
    }
  }

  async classifyPage(input: PageClassifierInput): Promise<PageClassification> {
    const response = await this.fetchImpl(`${this.baseUrl}/v1/classify-page`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        page_number: input.pageNumber,
        text: input.text || null,
        page_width: input.pageWidth ?? null,
        page_height: input.pageHeight ?? null,
        page_pdf_base64: input.pagePdf ? input.pagePdf.toString('base64') : null,
        image_png_base64: input.imagePng ? input.imagePng.toString('base64') : null
      }),
      signal: AbortSignal.timeout(this.timeoutMs)
    });

    if (!response.ok) {
      throw new Error(`VISION_SERVICE_ERROR:${response.status}`);
    }

    const body = (await response.json()) as VisionClassifyResponse;
    return {
      pageNumber: input.pageNumber,
      pageClass: parsePageClass(body.page_class),
      confidence: Math.min(1, Math.max(0, Number(body.confidence ?? 0))),
      carrier: typeof body.carrier === 'string' && body.carrier ? body.carrier : null,
      isReturn: Boolean(body.is_return),
      barcodes: parseBarcodes(body.barcodes),
      evidence: Array.isArray(body.evidence) ? body.evidence.filter((item): item is string => typeof item === 'string') : [],
      classifier: this.name
    };
  }
}

/**
 * Prefers the Vision Service and falls back to the local heuristic when the service
 * is unconfigured, unreachable, or errors — the pipeline must keep printing either way.
 */
export class CompositePageClassifier implements PageClassifier {
  public readonly name = 'composite';

  constructor(
    private readonly primary: PageClassifier | null,
    private readonly fallback: PageClassifier
  ) {}

  async classifyPage(input: PageClassifierInput): Promise<PageClassification> {
    if (this.primary) {
      try {
        return await this.primary.classifyPage(input);
      } catch (error) {
        // eslint-disable-next-line no-console
        console.warn(
          JSON.stringify({
            service: 'worker',
            event: 'vision_classify_fallback',
            pageNumber: input.pageNumber,
            error: error instanceof Error ? error.message : 'VISION_SERVICE_UNAVAILABLE'
          })
        );
      }
    }
    return this.fallback.classifyPage(input);
  }
}
