import { gunzipSync } from 'node:zlib';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  evaluateDocument,
  type DocumentDecision,
  type DocumentFeatures,
  type PageFeatures,
  type RoutingProfileRules
} from '../../src/index.js';

const here = dirname(fileURLToPath(import.meta.url));

/** `tests/corpus` at the repository root, where the extracted corpus lives. */
export const CORPUS_DIR = resolve(here, '../../../../tests/corpus');
export const FEATURES_PATH = resolve(CORPUS_DIR, 'features.jsonl.gz');
export const EXPECTED_PATH = resolve(CORPUS_DIR, 'expected.json');

export interface ExpectedPage {
  doc: string;
  pageNumber: number;
  pageClass: string;
  route: string;
  evidence: string[];
}

export interface ExpectedCorpus {
  pageCount: number;
  classCounts: Record<string, number>;
  routeCounts: Record<string, number>;
  pages: ExpectedPage[];
}

export function corpusAvailable(): boolean {
  return existsSync(FEATURES_PATH) && existsSync(EXPECTED_PATH);
}

/** Loads the extracted page features, grouped into documents in file order. */
export function loadCorpus(): DocumentFeatures[] {
  const raw = gunzipSync(readFileSync(FEATURES_PATH)).toString('utf8');
  const byDocument = new Map<string, PageFeatures[]>();

  for (const line of raw.split('\n')) {
    if (line.trim().length === 0) {
      continue;
    }
    const record = JSON.parse(line) as PageFeatures & { doc: string };
    const pages = byDocument.get(record.doc) ?? [];
    pages.push(record);
    byDocument.set(record.doc, pages);
  }

  return [...byDocument.entries()].map(([fileName, pages]) => ({
    fileName,
    pageCount: pages[0]?.pageCount ?? pages.length,
    pages: pages.sort((left, right) => left.pageNumber - right.pageNumber)
  }));
}

export function loadExpected(): ExpectedCorpus {
  return JSON.parse(readFileSync(EXPECTED_PATH, 'utf8')) as ExpectedCorpus;
}

/**
 * Returns a copy of the document with the embedded text layer removed.
 *
 * The anonymiser that produced the corpus *added* a text layer to the FedEx and UPS labels;
 * production originals are image-only. Any rule set that only passes with that synthetic
 * text layer is failing, so every corpus assertion runs in this mode too.
 */
export function stripTextLayer(document: DocumentFeatures): DocumentFeatures {
  return {
    ...document,
    pages: document.pages.map((page) => ({ ...page, text: null, textLines: undefined }))
  };
}

/**
 * Runs a document to a decision, servicing the engine's lazy feature requests.
 *
 * The corpus ships pre-extracted OCR, so a request should always be satisfiable; if one is
 * not, that is a real defect (a rule asking for OCR of a region nobody extracted) and the
 * helper throws rather than looping.
 */
export function decide(
  profile: RoutingProfileRules,
  document: DocumentFeatures
): DocumentDecision {
  const first = evaluateDocument(profile, document);
  if (first.status === 'decided') {
    return first.document;
  }

  const missing = first.ocr
    .map((request) => `p${request.pageNumber} ${request.key} (rule ${request.ruleId})`)
    .join('; ');
  throw new Error(
    `${document.fileName}: engine asked for OCR that the corpus does not contain: ${missing}`
  );
}
