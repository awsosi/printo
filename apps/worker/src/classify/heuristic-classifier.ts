import {
  DEFAULT_LABEL_DETECTION_SETTINGS,
  type LabelDetectionSettings,
  type PageClass,
  type PageClassification,
  type PageClassifier,
  type PageClassifierInput
} from './types.js';

interface CarrierSignature {
  carrier: string;
  patterns: RegExp[];
}

/**
 * Carrier signatures, most specific first.
 *
 * The GLS entry is deliberately guarded. Every MyDHL label prints the literal footer
 * `*GLS certified label*`, and a bare `/\bgls\b/i` reads that as a GLS shipment: 278 pages of
 * the reference corpus carry the footer. Most of them are rescued by chance, because the DHL
 * signatures match the same page and DHL is tested first — but a DHL *DOMESTIC EXPRESS* label
 * matches none of the old DHL patterns (`\bdhl\b` does not match "MyDHL"), falls through, and
 * is attributed to GLS. So the footer is registered as the DHL artifact it is, `MyDHL` and the
 * remaining DHL product names are recognised, and the GLS keyword only counts outside that
 * phrase.
 */
const BUILTIN_CARRIERS: CarrierSignature[] = [
  {
    carrier: 'DHL',
    patterns: [
      /\bdhl\b/i,
      /mydhl/i,
      /express worldwide/i,
      /economy select/i,
      /domestic express/i,
      /gls\s*certified\s*label/i,
      /\bpaket\b/i,
      /deutsche post/i
    ]
  },
  { carrier: 'UPS', patterns: [/\bups\b/i, /united parcel/i, /\b1Z[0-9A-Z]{15,16}\b/, /ups (standard|express|ground|saver)/i] },
  { carrier: 'FEDEX', patterns: [/fed\s?ex/i, /smartpost/i, /fedex (ground|express|international)/i] },
  { carrier: 'DPD', patterns: [/\bdpd\b/i, /dpd (classic|pickup)/i] },
  { carrier: 'GLS', patterns: [/\bgls\b(?!\s*certified\s*label)/i, /general logistics systems/i] },
  { carrier: 'INPOST', patterns: [/inpost/i, /paczkomat/i] },
  { carrier: 'POCZTA_POLSKA', patterns: [/poczta polska/i, /pocztex/i] }
];

const LABEL_KEYWORDS = [
  'ship to',
  'ship from',
  'shipper',
  'consignee',
  'tracking number',
  'tracking no',
  'tracking #',
  'waybill',
  'airway bill',
  'list przewozowy',
  'nadawca',
  'odbiorca',
  'przesyłka',
  'shipment date',
  'service level',
  'delivery address',
  'parcel',
  'package weight'
];

const RETURN_KEYWORDS = [
  'return label',
  'return shipment',
  'returns label',
  'retour',
  'retoure',
  'rücksendung',
  'rucksendung',
  'zwrot',
  'etykieta zwrotna',
  'etykieta zwrotu',
  'przesyłka zwrotna'
];

const DOCUMENT_KEYWORDS = [
  'invoice',
  'faktura',
  'vat',
  'iban',
  'payment terms',
  'termin płatności',
  'suma brutto',
  'total amount',
  'order confirmation',
  'packing slip',
  'specyfikacja'
];

const TRACKING_PATTERNS: Array<{ pattern: RegExp; evidence: string }> = [
  { pattern: /\b1Z[0-9A-Z]{15,16}\b/, evidence: 'tracking:ups-1z' },
  { pattern: /\bJJD[0-9]{16,20}\b/i, evidence: 'tracking:dhl-jjd' },
  { pattern: /\b\d{2}\s?\d{4}\s?\d{4}\s?\d{10}\b/, evidence: 'tracking:dhl-paket' },
  { pattern: /\b(96|00)\d{18,20}\b/, evidence: 'tracking:gs1-sscc' }
];

/** Points on a PDF page (1pt = 1/72in). 4×6in label stock is 288×432pt; treat anything close as label-sized. */
const LABEL_PAGE_MAX_WIDTH_PT = 340;
const LABEL_PAGE_MAX_HEIGHT_PT = 500;

function clamp01(value: number): number {
  return Math.min(1, Math.max(0, value));
}

function toPattern(raw: string): RegExp | null {
  const trimmed = raw.trim();
  if (!trimmed) {
    return null;
  }
  if (trimmed.startsWith('/') && trimmed.endsWith('/') && trimmed.length > 2) {
    try {
      return new RegExp(trimmed.slice(1, -1), 'i');
    } catch {
      return null;
    }
  }
  return new RegExp(trimmed.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i');
}

export interface HeuristicScore {
  pageClass: PageClass;
  confidence: number;
  carrier: string | null;
  isReturn: boolean;
  evidence: string[];
}

/**
 * Deterministic text/layout scoring shared by the worker fallback classifier and tests.
 * Mirrors the scoring implemented in the Vision Service so both agree on obvious pages.
 */
export function scorePageText(input: {
  text: string;
  pageWidth?: number;
  pageHeight?: number;
  settings?: LabelDetectionSettings;
}): HeuristicScore {
  const settings = input.settings ?? DEFAULT_LABEL_DETECTION_SETTINGS;
  const text = input.text.replace(/\s+/g, ' ').trim().toLowerCase();
  const evidence: string[] = [];
  let labelScore = 0;

  let carrier: string | null = null;
  const carrierSignatures: CarrierSignature[] = [
    ...BUILTIN_CARRIERS,
    ...Object.entries(settings.extraCarrierPatterns).map(([name, patterns]) => ({
      carrier: name,
      patterns: patterns.map(toPattern).filter((item): item is RegExp => item !== null)
    }))
  ];
  for (const signature of carrierSignatures) {
    if (signature.patterns.some((pattern) => pattern.test(text))) {
      carrier = signature.carrier;
      labelScore += 0.35;
      evidence.push(`carrier:${signature.carrier.toLowerCase()}`);
      break;
    }
  }

  const labelKeywords = [...LABEL_KEYWORDS, ...settings.extraLabelKeywords.map((keyword) => keyword.toLowerCase())];
  let keywordHits = 0;
  for (const keyword of labelKeywords) {
    if (keyword && text.includes(keyword)) {
      keywordHits += 1;
      evidence.push(`keyword:${keyword}`);
    }
  }
  labelScore += Math.min(0.45, keywordHits * 0.15);

  for (const { pattern, evidence: name } of TRACKING_PATTERNS) {
    if (pattern.test(input.text)) {
      labelScore += 0.25;
      evidence.push(name);
      break;
    }
  }

  if (
    input.pageWidth &&
    input.pageHeight &&
    input.pageWidth <= LABEL_PAGE_MAX_WIDTH_PT &&
    input.pageHeight <= LABEL_PAGE_MAX_HEIGHT_PT
  ) {
    labelScore += 0.3;
    evidence.push('layout:label-sized-page');
  }

  let documentHits = 0;
  for (const keyword of DOCUMENT_KEYWORDS) {
    if (text.includes(keyword)) {
      documentHits += 1;
      evidence.push(`document:${keyword}`);
    }
  }
  labelScore -= Math.min(0.5, documentHits * 0.25);

  const returnKeywords = [...RETURN_KEYWORDS, ...settings.extraReturnKeywords.map((keyword) => keyword.toLowerCase())];
  const returnHit = returnKeywords.find((keyword) => keyword && text.includes(keyword)) ?? null;
  if (returnHit) {
    evidence.push(`return:${returnHit}`);
  }

  const confidence = clamp01(labelScore);
  const isLabel = confidence >= settings.labelConfidenceThreshold;
  const pageClass: PageClass = !isLabel ? 'DOCUMENT_A4' : returnHit ? 'RETURN_LABEL_A4' : 'OUTGOING_LABEL_THERMAL';

  return {
    pageClass,
    confidence: isLabel ? confidence : clamp01(1 - confidence),
    carrier,
    isReturn: Boolean(returnHit),
    evidence
  };
}

/**
 * Rule-based classifier over the PDF text layer. Used as the deterministic CI path
 * and as the fallback when the Vision Service is unreachable. Pages without a text
 * layer (pure scans) come back as low-confidence DOCUMENT_A4 — those are exactly the
 * pages the Vision Service exists for.
 */
export class HeuristicPageClassifier implements PageClassifier {
  public readonly name = 'heuristic';

  constructor(private readonly settings: LabelDetectionSettings = DEFAULT_LABEL_DETECTION_SETTINGS) {}

  async classifyPage(input: PageClassifierInput): Promise<PageClassification> {
    if (!input.text.trim()) {
      return {
        pageNumber: input.pageNumber,
        pageClass: 'DOCUMENT_A4',
        confidence: 0.2,
        carrier: null,
        isReturn: false,
        barcodes: [],
        evidence: ['no-text-layer'],
        classifier: this.name
      };
    }

    const score = scorePageText({
      text: input.text,
      pageWidth: input.pageWidth,
      pageHeight: input.pageHeight,
      settings: this.settings
    });

    return {
      pageNumber: input.pageNumber,
      pageClass: score.pageClass,
      confidence: score.confidence,
      carrier: score.carrier,
      isReturn: score.isReturn,
      barcodes: [],
      evidence: score.evidence,
      classifier: this.name
    };
  }
}
