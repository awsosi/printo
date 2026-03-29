import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const pdfjs = require('pdfjs-dist/legacy/build/pdf.js') as {
  getDocument(input: {
    data: Uint8Array;
    useWorkerFetch: boolean;
    isEvalSupported: boolean;
    disableFontFace: boolean;
    standardFontDataUrl: string;
  }): { promise: Promise<PdfDocumentProxy> };
};

interface PdfDocumentProxy {
  numPages: number;
  getPage(pageNumber: number): Promise<PdfPageProxy>;
  destroy(): Promise<void>;
}

interface PdfPageProxy {
  getViewport(input: { scale: number }): { width: number; height: number };
  getTextContent(): Promise<{
    items: Array<{
      str?: string;
      width?: number;
      height?: number;
      transform?: number[];
    }>;
  }>;
}

export interface PdfTextItem {
  text: string;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface PdfExtractedPage {
  pageNumber: number;
  width: number;
  height: number;
  text: string;
  items: PdfTextItem[];
}

function normalizeText(value: string): string {
  return value.replace(/\s+/g, ' ').trim();
}

export async function extractPdfPages(buffer: Buffer): Promise<PdfExtractedPage[]> {
  const loadingTask = pdfjs.getDocument({
    data: new Uint8Array(buffer),
    useWorkerFetch: false,
    isEvalSupported: false,
    disableFontFace: true,
    standardFontDataUrl: ''
  });
  const document = await loadingTask.promise;

  try {
    const pages: PdfExtractedPage[] = [];
    for (let pageNumber = 1; pageNumber <= document.numPages; pageNumber += 1) {
      const page = await document.getPage(pageNumber);
      const viewport = page.getViewport({ scale: 1 });
      const textContent = await page.getTextContent();
      const items = textContent.items.flatMap((item) => {
        if (!item || typeof item.str !== 'string' || !Array.isArray(item.transform)) {
          return [];
        }
        const text = normalizeText(item.str);
        if (!text) {
          return [];
        }
        const [, , , , x, baselineY] = item.transform;
        const width = Number(item.width ?? 0);
        const height = Number(item.height ?? 0) || 10;
        return [
          {
            text,
            x: Number(x),
            y: viewport.height - Number(baselineY) - height,
            width,
            height
          }
        ];
      });
      pages.push({
        pageNumber,
        width: viewport.width,
        height: viewport.height,
        text: normalizeText(items.map((item) => item.text).join(' ')),
        items
      });
    }
    return pages;
  } finally {
    await document.destroy();
  }
}
