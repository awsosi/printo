import { createRequire } from 'node:module';
import { join } from 'node:path';

const require = createRequire(import.meta.url);

function loadOptionalModule<T>(candidates: string[]): T {
  for (const candidate of candidates) {
    try {
      return require(candidate) as T;
    } catch {
      // Try the next candidate.
    }
  }

  throw new Error(`MODULE_LOAD_FAILED:${candidates[0]}`);
}

const pdfjs = loadOptionalModule<{
  getDocument(input: {
    data: Uint8Array;
    useWorkerFetch: boolean;
    isEvalSupported: boolean;
    disableFontFace: boolean;
    standardFontDataUrl: string;
  }): { promise: Promise<PdfDocumentProxy> };
}>([
  'pdfjs-dist/legacy/build/pdf.js',
  join(process.cwd(), 'apps/worker/node_modules/pdfjs-dist/legacy/build/pdf.js'),
  join(process.cwd(), 'apps/web/node_modules/pdfjs-dist/legacy/build/pdf.js')
]);
interface CanvasModule {
  createCanvas(width: number, height: number): CanvasLike;
  loadImage(source: Buffer): Promise<{ width: number; height: number }>;
}

let cachedCanvasLib: CanvasModule | null = null;

// The native canvas module is only needed for the legacy image-snippet matcher.
// Loading it lazily keeps the worker (which now classifies via text heuristics
// and the Vision Service) runnable on hosts without the Cairo toolchain.
function loadCanvasLib(): CanvasModule {
  if (!cachedCanvasLib) {
    try {
      cachedCanvasLib = require('canvas') as CanvasModule;
    } catch (error) {
      throw new Error(
        `CANVAS_UNAVAILABLE: image-snippet matching requires the optional native 'canvas' module (${
          error instanceof Error ? error.message : 'load failed'
        })`
      );
    }
  }
  return cachedCanvasLib;
}

interface CanvasLike {
  width: number;
  height: number;
  getContext(kind: '2d'): CanvasContextLike;
}

interface CanvasContextLike {
  drawImage(image: { width: number; height: number }, dx: number, dy: number, dw: number, dh: number): void;
  getImageData(x: number, y: number, width: number, height: number): { data: Uint8ClampedArray };
}

interface PdfDocumentProxy {
  numPages: number;
  getPage(pageNumber: number): Promise<PdfPageProxy>;
  destroy(): Promise<void>;
}

interface PdfPageProxy {
  getViewport(input: { scale: number }): { width: number; height: number };
  render(input: {
    canvasContext: CanvasContextLike;
    viewport: { width: number; height: number };
  }): { promise: Promise<void> };
}

export interface PdfSnippetMatchPage {
  pageNumber: number;
  score: number;
  isMatch: boolean;
}

export interface PdfSnippetMatchResult {
  pages: PdfSnippetMatchPage[];
}

interface GrayImage {
  width: number;
  height: number;
  pixels: Float32Array;
}

const DEFAULT_RENDER_WIDTH = 320;
const DEFAULT_MATCH_THRESHOLD = 0.88;
const SCALE_FACTORS = Array.from({ length: 21 }, (_value, index) => Number((0.35 + index * 0.05).toFixed(2)));

function toGrayImage(width: number, height: number, rgba: Uint8ClampedArray): GrayImage {
  const pixels = new Float32Array(width * height);
  for (let index = 0; index < width * height; index += 1) {
    const offset = index * 4;
    const r = rgba[offset] ?? 0;
    const g = rgba[offset + 1] ?? 0;
    const b = rgba[offset + 2] ?? 0;
    const alpha = (rgba[offset + 3] ?? 255) / 255;
    const onWhite = (0.299 * r + 0.587 * g + 0.114 * b) * alpha + 255 * (1 - alpha);
    pixels[index] = onWhite;
  }
  return { width, height, pixels };
}

function resizeGrayImage(image: GrayImage, scale: number): GrayImage {
  const width = Math.max(1, Math.round(image.width * scale));
  const height = Math.max(1, Math.round(image.height * scale));
  const pixels = new Float32Array(width * height);

  for (let y = 0; y < height; y += 1) {
    const sourceY = Math.min(image.height - 1, Math.round((y / height) * image.height));
    for (let x = 0; x < width; x += 1) {
      const sourceX = Math.min(image.width - 1, Math.round((x / width) * image.width));
      pixels[y * width + x] = image.pixels[sourceY * image.width + sourceX];
    }
  }

  return { width, height, pixels };
}

function prepareTemplateStats(template: GrayImage) {
  let sum = 0;
  let sumSquares = 0;
  for (let index = 0; index < template.pixels.length; index += 1) {
    const value = template.pixels[index]!;
    sum += value;
    sumSquares += value * value;
  }

  return { sum, sumSquares, area: template.pixels.length };
}

function scoreAtPosition(page: GrayImage, template: GrayImage, stats: ReturnType<typeof prepareTemplateStats>, startX: number, startY: number) {
  let pageSum = 0;
  let pageSumSquares = 0;
  let crossSum = 0;

  for (let y = 0; y < template.height; y += 1) {
    const pageOffset = (startY + y) * page.width + startX;
    const templateOffset = y * template.width;
    for (let x = 0; x < template.width; x += 1) {
      const pageValue = page.pixels[pageOffset + x]!;
      const templateValue = template.pixels[templateOffset + x]!;
      pageSum += pageValue;
      pageSumSquares += pageValue * pageValue;
      crossSum += pageValue * templateValue;
    }
  }

  const area = stats.area;
  const templateMean = stats.sum / area;
  const pageMean = pageSum / area;
  const numerator = crossSum - area * pageMean * templateMean;
  const pageVariance = pageSumSquares - area * pageMean * pageMean;
  const templateVariance = stats.sumSquares - area * templateMean * templateMean;
  const denominator = Math.sqrt(Math.max(pageVariance, 0) * Math.max(templateVariance, 0));

  if (!Number.isFinite(denominator) || denominator <= 1e-6) {
    return 0;
  }

  const normalized = numerator / denominator;
  return Math.max(0, Math.min(1, (normalized + 1) / 2));
}

function findBestTemplateScore(page: GrayImage, template: GrayImage): number {
  if (template.width > page.width || template.height > page.height) {
    return 0;
  }

  const stats = prepareTemplateStats(template);
  const step = Math.max(1, Math.floor(Math.min(template.width, template.height) / 12));
  let best = 0;

  for (let y = 0; y <= page.height - template.height; y += step) {
    for (let x = 0; x <= page.width - template.width; x += step) {
      const score = scoreAtPosition(page, template, stats, x, y);
      if (score > best) {
        best = score;
      }
    }
  }

  return best;
}

async function decodeSnippet(snippetBase64: string): Promise<GrayImage> {
  const buffer = Buffer.from(snippetBase64, 'base64');
  const canvasLib = loadCanvasLib();
  const image = await canvasLib.loadImage(buffer);
  const canvas = canvasLib.createCanvas(image.width, image.height);
  const context = canvas.getContext('2d');
  context.drawImage(image, 0, 0, image.width, image.height);
  return toGrayImage(image.width, image.height, context.getImageData(0, 0, image.width, image.height).data);
}

async function renderPdfPages(pdfBuffer: Buffer): Promise<GrayImage[]> {
  const loadingTask = pdfjs.getDocument({
    data: new Uint8Array(pdfBuffer),
    useWorkerFetch: false,
    isEvalSupported: false,
    disableFontFace: true,
    standardFontDataUrl: ''
  });
  const document = await loadingTask.promise;

  try {
    const pages: GrayImage[] = [];
    for (let pageNumber = 1; pageNumber <= document.numPages; pageNumber += 1) {
      const page = await document.getPage(pageNumber);
      const baseViewport = page.getViewport({ scale: 1 });
      const scale = DEFAULT_RENDER_WIDTH / baseViewport.width;
      const viewport = page.getViewport({ scale });
      const canvas = loadCanvasLib().createCanvas(Math.max(1, Math.round(viewport.width)), Math.max(1, Math.round(viewport.height)));
      const context = canvas.getContext('2d');
      await page.render({ canvasContext: context, viewport }).promise;
      const imageData = context.getImageData(0, 0, canvas.width, canvas.height).data;
      pages.push(toGrayImage(canvas.width, canvas.height, imageData));
    }
    return pages;
  } finally {
    await document.destroy();
  }
}

function scoreSnippetAgainstPage(page: GrayImage, snippet: GrayImage): number {
  let best = 0;
  for (const scale of SCALE_FACTORS) {
    const candidate = resizeGrayImage(snippet, scale);
    const score = findBestTemplateScore(page, candidate);
    if (score > best) {
      best = score;
    }
  }
  return best;
}

export async function matchPdfPagesBySnippet(input: {
  pdfBuffer: Buffer;
  snippetBase64: string;
  matchThreshold?: number;
}): Promise<PdfSnippetMatchResult> {
  const threshold = input.matchThreshold ?? DEFAULT_MATCH_THRESHOLD;
  const [snippet, pages] = await Promise.all([decodeSnippet(input.snippetBase64), renderPdfPages(input.pdfBuffer)]);

  return {
    pages: pages.map((page, index) => {
      const score = scoreSnippetAgainstPage(page, snippet);
      return {
        pageNumber: index + 1,
        score: Number(score.toFixed(4)),
        isMatch: score >= threshold
      };
    })
  };
}
