import { describe, expect, it } from 'vitest';
import { resolveCarrier, type PageFeatures } from '../src/index.js';

function page(overrides: Partial<PageFeatures> = {}): PageFeatures {
  return {
    pageNumber: 1,
    pageCount: 1,
    pageWidthMm: 297,
    pageHeightMm: 210,
    orientation: 'landscape',
    rotation: 0,
    text: null,
    inkBox: { xMm: 34, yMm: 15, widthMm: 92, heightMm: 180, aspect: 1.96, coverage: 0.065 },
    barcodes: [],
    ...overrides
  };
}

describe('carrier resolution', () => {
  it('does not read a DHL label as GLS because of the certified-label footer', () => {
    // This exact string appears on every MyDHL label in the corpus and is what makes the
    // current worker heuristic mis-attribute 278 pages to GLS.
    const resolution = resolveCarrier(
      page({
        text:
          'EXPRESS WORLDWIDE\n2026-08-20 MyDHL API 1.0 / *GLS certified label* WPX\n' +
          'From : CTD - see data\nOrigin: WAW'
      })
    );

    expect(resolution.carrier).toBe('DHL');
    expect(resolution.scores.find((score) => score.carrier === 'GLS')).toBeUndefined();
  });

  it('still recognises a genuine GLS shipment', () => {
    const resolution = resolveCarrier(
      page({ text: 'General Logistics Systems Germany GmbH\nGLS ParcelShop\nwww.gls-group.eu' })
    );
    expect(resolution.carrier).toBe('GLS');
  });

  it('lets a decoded waybill number outrank a bare keyword', () => {
    // A DHL label that happens to mention UPS in a free-text reference must still be DHL.
    const resolution = resolveCarrier(
      page({
        text: 'Ref No: collected by UPS driver',
        barcodes: [
          {
            symbology: 'Code128',
            value: 'JD014600009354770923',
            xMm: 10,
            yMm: 150,
            widthMm: 70,
            heightMm: 15
          }
        ]
      })
    );

    expect(resolution.carrier).toBe('DHL');
    expect(resolution.confidence).toBeGreaterThanOrEqual(0.9);
    expect(resolution.evidence.some((item) => item.source === 'barcode')).toBe(true);
  });

  it('resolves UPS from a MaxiCode plus a 1Z tracking number', () => {
    const resolution = resolveCarrier(
      page({
        barcodes: [
          { symbology: 'MaxiCode', value: '[)>', xMm: 10, yMm: 10, widthMm: 25, heightMm: 25 },
          {
            symbology: 'Code128',
            value: '1Z7273X60155210490',
            xMm: 10,
            yMm: 60,
            widthMm: 70,
            heightMm: 15
          }
        ]
      })
    );
    expect(resolution.carrier).toBe('UPS');
    expect(resolution.confidence).toBeGreaterThanOrEqual(0.9);
  });

  it('reads carrier evidence out of OCR when there is no text layer', () => {
    const resolution = resolveCarrier(
      page({
        ocrRegions: [
          {
            key: '34.0,15.0,92.0,180.0',
            rect: { xMm: 34, yMm: 15, widthMm: 92, heightMm: 180 },
            text: 'ECONOMY SELECT\nCTD see data\nWAYBILL 83 0121 8004'
          }
        ]
      })
    );
    expect(resolution.carrier).toBe('DHL');
  });

  it('reports no carrier rather than guessing on a blank page', () => {
    const resolution = resolveCarrier(page({ text: 'Sales Invoice\nTotal amount 177.62' }));
    expect(resolution.carrier).toBeNull();
    expect(resolution.confidence).toBe(0);
  });

  it('lowers confidence when two carriers score close together', () => {
    const resolution = resolveCarrier(
      page({ text: 'Handled by DPD on behalf of GLS ParcelShop network' })
    );
    expect(resolution.confidence).toBeLessThanOrEqual(0.5);
    expect(resolution.scores.length).toBeGreaterThan(1);
  });
});
