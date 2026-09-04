/**
 * Carrier resolution.
 *
 * This is its own scored step rather than a keyword match inside a rule, because keyword
 * soup is exactly what breaks today: every DHL MyDHL label in the corpus carries the literal
 * string `*GLS certified label*`, and the worker's `/\bgls\b/i` therefore mis-attributes 278
 * pages to GLS. The fix is structural, not a bigger keyword list:
 *
 *   1. evidence is weighted by source — a decoded waybill barcode outranks a bare keyword;
 *   2. `GLS certified label` is registered as what it actually is, a DHL artifact;
 *   3. the bare `GLS` keyword only counts when it is *not* part of that phrase;
 *   4. every score is reported, so a near-miss between two carriers is visible in the trace
 *      rather than silently resolved.
 */

import type { PageFeatures } from './features.js';
import type { CarrierEvidence, CarrierResolution } from './trace.js';

/** Where a piece of carrier evidence was found. Order matters: it sets the weight ceiling. */
export type CarrierSignalSource = 'barcodeValue' | 'barcodeSymbology' | 'text' | 'ocr';

export interface CarrierSignal {
  source: CarrierSignalSource;
  /** Regex source for value/text sources; an exact symbology name for `barcodeSymbology`. */
  pattern: string;
  weight: number;
  /** Short human-readable name used in the trace. */
  label: string;
}

export interface CarrierSignatureSet {
  carrier: string;
  signals: CarrierSignal[];
}

/**
 * Built-in carrier signatures. Deployments extend this from the bundle rather than editing
 * code; the defaults cover the three carriers present in the corpus plus the European
 * carriers the product is expected to meet next.
 */
export const BUILTIN_CARRIER_SIGNATURES: CarrierSignatureSet[] = [
  {
    carrier: 'DHL',
    signals: [
      { source: 'barcodeValue', pattern: '^JD\\d{16,20}$', weight: 0.95, label: 'waybill JD number' },
      { source: 'barcodeValue', pattern: '^JJD\\d{16,20}$', weight: 0.95, label: 'waybill JJD number' },
      { source: 'text', pattern: 'EXPRESS\\s*WORLDWIDE', weight: 0.6, label: 'EXPRESS WORLDWIDE' },
      { source: 'text', pattern: 'ECONOMY\\s*SELECT', weight: 0.6, label: 'ECONOMY SELECT' },
      { source: 'text', pattern: 'MyDHL', weight: 0.6, label: 'MyDHL' },
      // Only ever printed by MyDHL, on DHL stock. Registering it here is what stops it
      // being read as a GLS shipment.
      { source: 'text', pattern: 'GLS\\s*certified\\s*label', weight: 0.5, label: 'MyDHL certified-label footer' },
      { source: 'text', pattern: '\\bWAYBILL\\b', weight: 0.3, label: 'WAYBILL' },
      { source: 'text', pattern: '\\bDHL\\b', weight: 0.35, label: 'DHL' },
      { source: 'ocr', pattern: 'EXPRESS\\s*WORLDWIDE', weight: 0.55, label: 'EXPRESS WORLDWIDE (ocr)' },
      { source: 'ocr', pattern: 'ECONOMY\\s*SELECT', weight: 0.55, label: 'ECONOMY SELECT (ocr)' },
      { source: 'ocr', pattern: 'MyDHL', weight: 0.55, label: 'MyDHL (ocr)' },
      { source: 'ocr', pattern: '\\bDHL\\b', weight: 0.3, label: 'DHL (ocr)' }
    ]
  },
  {
    carrier: 'UPS',
    signals: [
      { source: 'barcodeValue', pattern: '^1Z[0-9A-Z]{16}$', weight: 0.95, label: '1Z tracking number' },
      { source: 'barcodeSymbology', pattern: 'MaxiCode', weight: 0.7, label: 'MaxiCode' },
      { source: 'text', pattern: 'United\\s*Parcel', weight: 0.6, label: 'United Parcel Service' },
      { source: 'text', pattern: '\\b1Z\\s?[0-9A-Z]{3}\\s?[0-9A-Z]{3}\\s?[0-9A-Z]{2}\\s?\\d{4}\\s?\\d{4}\\b', weight: 0.7, label: '1Z tracking number (text)' },
      { source: 'text', pattern: 'ups\\.com', weight: 0.5, label: 'ups.com' },
      { source: 'text', pattern: '\\bUPS\\b', weight: 0.35, label: 'UPS' },
      { source: 'ocr', pattern: '\\bUPS\\b', weight: 0.3, label: 'UPS (ocr)' },
      { source: 'ocr', pattern: 'United\\s*Parcel', weight: 0.55, label: 'United Parcel Service (ocr)' }
    ]
  },
  {
    carrier: 'FEDEX',
    signals: [
      { source: 'barcodeValue', pattern: '^\\[\\)>.*?FDE', weight: 0.9, label: 'FedEx PDF417 header' },
      { source: 'text', pattern: 'Fed\\s?Ex', weight: 0.6, label: 'FedEx' },
      { source: 'text', pattern: '\\bTRK#', weight: 0.4, label: 'TRK#' },
      { source: 'text', pattern: 'BILL\\s+SENDER', weight: 0.35, label: 'BILL SENDER' },
      { source: 'ocr', pattern: 'Fed\\s?Ex', weight: 0.55, label: 'FedEx (ocr)' },
      { source: 'ocr', pattern: '\\bTRK#', weight: 0.35, label: 'TRK# (ocr)' }
    ]
  },
  {
    carrier: 'GLS',
    signals: [
      // Deliberately guarded: `*GLS certified label*` is DHL's, not GLS's.
      { source: 'text', pattern: '\\bGLS\\b(?!\\s*certified)', weight: 0.3, label: 'GLS' },
      { source: 'text', pattern: 'General\\s+Logistics\\s+Systems', weight: 0.7, label: 'General Logistics Systems' },
      { source: 'text', pattern: 'gls-group\\.', weight: 0.6, label: 'gls-group' },
      { source: 'ocr', pattern: 'General\\s+Logistics\\s+Systems', weight: 0.65, label: 'General Logistics Systems (ocr)' }
    ]
  },
  {
    carrier: 'DPD',
    signals: [
      { source: 'text', pattern: '\\bDPD\\b', weight: 0.4, label: 'DPD' },
      { source: 'text', pattern: 'dpd\\.(com|de|pl)', weight: 0.6, label: 'dpd domain' },
      { source: 'ocr', pattern: '\\bDPD\\b', weight: 0.35, label: 'DPD (ocr)' }
    ]
  },
  {
    carrier: 'INPOST',
    signals: [
      { source: 'text', pattern: 'InPost', weight: 0.6, label: 'InPost' },
      { source: 'text', pattern: 'Paczkomat', weight: 0.6, label: 'Paczkomat' },
      { source: 'ocr', pattern: 'InPost', weight: 0.55, label: 'InPost (ocr)' }
    ]
  },
  {
    carrier: 'POCZTA_POLSKA',
    signals: [
      { source: 'text', pattern: 'Poczta\\s+Polska', weight: 0.7, label: 'Poczta Polska' },
      { source: 'text', pattern: 'Pocztex', weight: 0.6, label: 'Pocztex' },
      { source: 'ocr', pattern: 'Poczta\\s+Polska', weight: 0.65, label: 'Poczta Polska (ocr)' }
    ]
  }
];

/** Two carriers scoring within this of each other is reported as an ambiguous resolution. */
const AMBIGUITY_MARGIN = 0.15;

function compile(pattern: string): RegExp | null {
  try {
    return new RegExp(pattern, 'i');
  } catch {
    return null;
  }
}

/** All OCR text available for the page, joined. */
function ocrText(page: PageFeatures): string {
  return (page.ocrRegions ?? []).map((region) => region.text).join('\n');
}

/**
 * Score every carrier against the page and return the best, with all evidence.
 *
 * Scores saturate rather than sum without bound: three weak keywords must not out-vote one
 * decoded waybill number.
 */
export function resolveCarrier(
  page: PageFeatures,
  signatures: CarrierSignatureSet[] = BUILTIN_CARRIER_SIGNATURES
): CarrierResolution {
  const text = page.text ?? '';
  const ocr = ocrText(page);
  const scored: Array<{ carrier: string; score: number; evidence: CarrierEvidence[] }> = [];

  for (const signature of signatures) {
    const evidence: CarrierEvidence[] = [];
    let best = 0;
    let total = 0;

    for (const signal of signature.signals) {
      let hit = false;

      if (signal.source === 'barcodeSymbology') {
        hit = page.barcodes.some(
          (barcode) => barcode.symbology.toLowerCase() === signal.pattern.toLowerCase()
        );
      } else {
        const regex = compile(signal.pattern);
        if (!regex) {
          continue;
        }

        if (signal.source === 'barcodeValue') {
          hit = page.barcodes.some((barcode) => regex.test(barcode.value));
        } else if (signal.source === 'text') {
          hit = text.length > 0 && regex.test(text);
        } else {
          hit = ocr.length > 0 && regex.test(ocr);
        }
      }

      if (hit) {
        evidence.push({
          source:
            signal.source === 'barcodeValue' || signal.source === 'barcodeSymbology'
              ? 'barcode'
              : signal.source,
          detail: signal.label,
          weight: signal.weight
        });
        best = Math.max(best, signal.weight);
        total += signal.weight;
      }
    }

    if (evidence.length > 0) {
      // The strongest single piece of evidence dominates; corroboration adds a little.
      const score = Math.min(1, best + (total - best) * 0.25);
      scored.push({ carrier: signature.carrier, score, evidence });
    }
  }

  scored.sort((left, right) => right.score - left.score);

  if (scored.length === 0) {
    return { carrier: null, confidence: 0, evidence: [], scores: [] };
  }

  const winner = scored[0];
  const runnerUp = scored[1];
  const ambiguous = runnerUp !== undefined && winner.score - runnerUp.score < AMBIGUITY_MARGIN;

  return {
    carrier: winner.carrier,
    // An unresolved tie is reported as low confidence rather than as a coin toss.
    confidence: ambiguous ? Math.min(winner.score, 0.5) : winner.score,
    evidence: winner.evidence,
    scores: scored.map((entry) => ({
      carrier: entry.carrier,
      score: Number(entry.score.toFixed(3))
    }))
  };
}
