import { describe, expect, it } from 'vitest';
import { HeuristicPageClassifier, scorePageText } from '../src/classify/heuristic-classifier.js';
import { CompositePageClassifier, VisionServiceClassifier } from '../src/classify/vision-classifier.js';
import type { PageClassification, PageClassifier, PageClassifierInput } from '../src/classify/types.js';

const classifier = new HeuristicPageClassifier();

describe('HeuristicPageClassifier', () => {
  it('classifies a DHL outgoing label page as OUTGOING_LABEL_THERMAL', async () => {
    const result = await classifier.classifyPage({
      pageNumber: 1,
      text: 'DHL EXPRESS WORLDWIDE Ship to: Jan Kowalski Waybill 12 3456 7890 1234567890 Tracking number JJD0099887766554433'
    });

    expect(result.pageClass).toBe('OUTGOING_LABEL_THERMAL');
    expect(result.carrier).toBe('DHL');
    expect(result.isReturn).toBe(false);
    expect(result.confidence).toBeGreaterThanOrEqual(0.5);
    expect(result.evidence).toContain('carrier:dhl');
  });

  it('classifies a UPS return label page as RETURN_LABEL_A4', async () => {
    const result = await classifier.classifyPage({
      pageNumber: 2,
      text: 'UPS RETURN LABEL Ship to: Returns Center Tracking number 1Z999AA10123456784 shipper'
    });

    expect(result.pageClass).toBe('RETURN_LABEL_A4');
    expect(result.carrier).toBe('UPS');
    expect(result.isReturn).toBe(true);
  });

  it('classifies an invoice page as DOCUMENT_A4', async () => {
    const result = await classifier.classifyPage({
      pageNumber: 3,
      text: 'Faktura VAT nr 2026/07/001 Suma brutto 1234,56 PLN IBAN PL61109010140000071219812874 Termin płatności 14 dni'
    });

    expect(result.pageClass).toBe('DOCUMENT_A4');
  });

  it('does not misroute an invoice that mentions a carrier name', async () => {
    const result = await classifier.classifyPage({
      pageNumber: 4,
      text: 'Invoice 2026/07/002 shipping via DHL total amount 99.00 EUR VAT 23% payment terms 30 days'
    });

    expect(result.pageClass).toBe('DOCUMENT_A4');
  });

  it('treats a label-sized page with a carrier as a label even with few keywords', async () => {
    const result = await classifier.classifyPage({
      pageNumber: 1,
      text: 'DPD Classic 0526 Parcel',
      pageWidth: 288,
      pageHeight: 432
    });

    expect(result.pageClass).toBe('OUTGOING_LABEL_THERMAL');
    expect(result.evidence).toContain('layout:label-sized-page');
  });

  it('returns low-confidence DOCUMENT_A4 for pages without a text layer', async () => {
    const result = await classifier.classifyPage({ pageNumber: 5, text: '' });

    expect(result.pageClass).toBe('DOCUMENT_A4');
    expect(result.confidence).toBeLessThan(0.5);
    expect(result.evidence).toContain('no-text-layer');
  });
});

describe('VisionServiceClassifier', () => {
  it('maps the service response to a PageClassification', async () => {
    const fetchImpl = (async (url: RequestInfo | URL, init?: RequestInit) => {
      expect(String(url)).toBe('http://vision:6000/v1/classify-page');
      const body = JSON.parse(String(init?.body));
      expect(body.page_number).toBe(7);
      return new Response(
        JSON.stringify({
          page_class: 'OUTGOING_LABEL_THERMAL',
          confidence: 0.93,
          carrier: 'UPS',
          is_return: false,
          barcodes: [{ symbology: 'MaxiCode', value: null, bounding_box: { x: 1, y: 2, width: 30, height: 30 } }],
          evidence: ['barcode:maxicode']
        }),
        { status: 200, headers: { 'content-type': 'application/json' } }
      );
    }) as typeof fetch;

    const vision = new VisionServiceClassifier({ baseUrl: 'http://vision:6000/', fetchImpl });
    const result = await vision.classifyPage({ pageNumber: 7, text: '' });

    expect(result.pageClass).toBe('OUTGOING_LABEL_THERMAL');
    expect(result.confidence).toBeCloseTo(0.93);
    expect(result.carrier).toBe('UPS');
    expect(result.barcodes).toHaveLength(1);
    expect(result.barcodes[0].symbology).toBe('MaxiCode');
    expect(result.classifier).toBe('vision-service');
  });

  it('throws on non-2xx responses', async () => {
    const fetchImpl = (async () => new Response('boom', { status: 503 })) as typeof fetch;
    const vision = new VisionServiceClassifier({ baseUrl: 'http://vision:6000', fetchImpl });

    await expect(vision.classifyPage({ pageNumber: 1, text: 'x' })).rejects.toThrow('VISION_SERVICE_ERROR:503');
  });
});

describe('CompositePageClassifier', () => {
  const stub = (name: string, pageClass: PageClassification['pageClass'], fail = false): PageClassifier => ({
    name,
    async classifyPage(input: PageClassifierInput): Promise<PageClassification> {
      if (fail) {
        throw new Error('unreachable');
      }
      return {
        pageNumber: input.pageNumber,
        pageClass,
        confidence: 0.9,
        carrier: null,
        isReturn: false,
        barcodes: [],
        evidence: [],
        classifier: name
      };
    }
  });

  it('uses the primary classifier when it succeeds', async () => {
    const composite = new CompositePageClassifier(stub('vision', 'OUTGOING_LABEL_THERMAL'), stub('heuristic', 'DOCUMENT_A4'));
    const result = await composite.classifyPage({ pageNumber: 1, text: 'x' });
    expect(result.classifier).toBe('vision');
  });

  it('falls back to the secondary classifier when the primary fails', async () => {
    const composite = new CompositePageClassifier(stub('vision', 'OUTGOING_LABEL_THERMAL', true), stub('heuristic', 'DOCUMENT_A4'));
    const result = await composite.classifyPage({ pageNumber: 1, text: 'x' });
    expect(result.classifier).toBe('heuristic');
    expect(result.pageClass).toBe('DOCUMENT_A4');
  });
});

describe('carrier attribution regression: the MyDHL certified-label footer', () => {
  // Every MyDHL label prints the literal `*GLS certified label*`; 278 pages of the reference
  // corpus carry it. Reading that as a GLS shipment is the defect this guards.
  const domesticExpress =
    'DOMESTIC EXPRESS\n2026-08-19 MyDHL API 1.0 / *GLS certified label* DOM\n' +
    'From : CTD - see data Origin: WAW\nWAYBILL\nRef Code:';

  it('attributes a DHL DOMESTIC EXPRESS label to DHL, not GLS', () => {
    // The old patterns missed this one entirely: `\bdhl\b` does not match "MyDHL", and the
    // product name is not "EXPRESS WORLDWIDE", so the page fell through to the GLS keyword.
    const score = scorePageText({ text: domesticExpress });
    expect(score.carrier).toBe('DHL');
  });

  it('attributes an EXPRESS WORLDWIDE label carrying the footer to DHL', () => {
    const score = scorePageText({
      text: 'EXPRESS WORLDWIDE\n2026-08-20 MyDHL API 1.0 / *GLS certified label* WPX\nFrom : CTD - see data'
    });
    expect(score.carrier).toBe('DHL');
  });

  it('does not read the footer alone as a GLS shipment', () => {
    const score = scorePageText({ text: 'Ref Code: 1234\n*GLS certified label*\nPce/Shpt Weight' });
    expect(score.carrier).not.toBe('GLS');
  });

  it('still recognises a genuine GLS shipment', () => {
    const score = scorePageText({
      text: 'GLS ParcelShop\nGeneral Logistics Systems Germany GmbH\nEmpfaenger: Anna Nowak'
    });
    expect(score.carrier).toBe('GLS');
  });
});
