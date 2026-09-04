import { describe, expect, it } from 'vitest';
import {
  evaluateDocument,
  evaluatePage,
  findFirstFailure,
  ONE_CLICK_PRINT_PROFILE,
  type DocumentFeatures,
  type PageFeatures,
  type RoutingProfileRules
} from '../src/index.js';

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

function document(pages: PageFeatures[], fileName = 'OneClickPrint_TEST.pdf'): DocumentFeatures {
  return {
    fileName,
    pageCount: pages.length,
    pages: pages.map((entry, index) => ({
      ...entry,
      pageNumber: entry.pageNumber ?? index + 1,
      pageCount: pages.length
    }))
  };
}

function decidedPage(features: PageFeatures, profile = ONE_CLICK_PRINT_PROFILE) {
  const result = evaluatePage(profile, features, document([features]), {});
  if (result.status !== 'decided') {
    throw new Error(`expected a decision, got a feature request: ${JSON.stringify(result.ocr)}`);
  }
  return result.decision;
}

describe('lazy feature requests', () => {
  it('asks for OCR only after the cheap rules have failed', () => {
    // A DHL-shaped page with no text layer reaches the OCR gate.
    const result = evaluatePage(
      ONE_CLICK_PRINT_PROFILE,
      page(),
      document([page()]),
      {}
    );
    expect(result.status).toBe('needs-features');
    if (result.status === 'needs-features') {
      expect(result.ocr).toHaveLength(1);
      expect(result.ocr[0].ruleId).toBe('dhl-waybill-sheet-ocr');
      expect(result.ocr[0].rect).toEqual({ xMm: 34, yMm: 15, widthMm: 92, heightMm: 180 });
    }
  });

  it('never asks for OCR when the text layer already decided the page', () => {
    const decision = decidedPage(
      page({ text: '*WAYBILL DOC* Not to be attached to package - Hand to Courier' })
    );
    expect(decision.route).toBe('A4');
    expect(decision.ruleId).toBe('dhl-waybill-sheet-text');
    expect(decision.trace.ocrRectsUsed).toEqual([]);
  });

  it('never asks for OCR for a FedEx label, whose geometry is decisive', () => {
    const decision = decidedPage(
      page({
        inkBox: { xMm: 34.3, yMm: 19.3, widthMm: 101.1, heightMm: 149.9, aspect: 1.483, coverage: 0.064 }
      })
    );
    expect(decision.route).toBe('THERMAL');
    expect(decision.ruleId).toBe('fedex-label-embedded');
    expect(decision.trace.ocrRectsUsed).toEqual([]);
  });

  it('resolves the waybill sheet from OCR once the region is supplied', () => {
    const features = page({
      ocrRegions: [
        {
          key: '34.0,15.0,92.0,180.0',
          rect: { xMm: 34, yMm: 15, widthMm: 92, heightMm: 180 },
          // The recogniser loses the spaces in the bold header; the rule must still match.
          text: '*WAYBILLDOC*\nNot to be attached to package -Hand to Courier'
        }
      ]
    });
    const decision = decidedPage(features);
    expect(decision.route).toBe('A4');
    expect(decision.ruleId).toBe('dhl-waybill-sheet-ocr');
    expect(decision.trace.ocrRectsUsed).toEqual(['34.0,15.0,92.0,180.0']);
  });

  it('routes a DHL label to thermal when OCR finds no waybill markings', () => {
    const decision = decidedPage(
      page({
        ocrRegions: [
          {
            key: '34.0,15.0,92.0,180.0',
            rect: { xMm: 34, yMm: 15, widthMm: 92, heightMm: 180 },
            text: 'ECONOMYSELECT\nCTD see data\nWAYBILL 83 0121 8004'
          }
        ]
      })
    );
    expect(decision.route).toBe('THERMAL');
    expect(decision.ruleId).toBe('dhl-label-embedded');
    expect(decision.transform?.source).toBe('inkBox');
  });
});

describe('traces', () => {
  it('records the measured value that made a predicate fail', () => {
    const decision = decidedPage(
      page({
        inkBox: { xMm: 34.3, yMm: 19.3, widthMm: 101.1, heightMm: 149.9, aspect: 1.483, coverage: 0.064 }
      })
    );

    const dhlRule = decision.trace.rules.find((rule) => rule.ruleId === 'dhl-label-stock');
    expect(dhlRule?.matched).toBe(false);
    const failure = dhlRule?.firstFailure;
    expect(failure).toBeDefined();
    // The trace names the predicate that failed *and* the number that failed it, which is
    // what turns "routing failed" into a rule an admin can fix in one edit.
    expect(failure?.kind).toBe('geometry');
    expect(String(failure?.detail)).toContain('inkAspect 1.7..2.2');
    expect(failure?.measured).toBe(1.48);
  });

  it('reports geometry and carrier scores for every page', () => {
    const decision = decidedPage(page({ text: 'Sales Invoice', pageWidthMm: 210, pageHeightMm: 297, orientation: 'portrait', inkBox: { xMm: 10, yMm: 16, widthMm: 190, heightMm: 234, aspect: 1.23, coverage: 0.06 } }));
    expect(decision.trace.geometry.inkAspect).toBe(1.23);
    expect(decision.trace.geometry.orientation).toBe('portrait');
    expect(decision.route).toBe('A4');
    expect(decision.ruleId).toBeNull();
  });

  it('finds the first failing leaf inside a nested condition', () => {
    const profile: RoutingProfileRules = {
      profile: 'test',
      pageRules: [
        {
          id: 'nested',
          name: 'nested',
          when: {
            all: [
              { geometry: { orientation: 'landscape' } },
              { any: [{ text: { contains: 'nope' } }, { barcode: { minCount: 3 } }] }
            ]
          },
          then: { route: 'THERMAL' }
        }
      ],
      fallback: { route: 'A4', onUnknown: 'route' }
    };

    const decision = decidedPage(page({ text: 'something else' }), profile);
    const trace = decision.trace.rules[0];
    expect(trace.matched).toBe(false);
    const failure = findFirstFailure(trace.predicate!);
    expect(failure?.path).toContain('any[0].text');
  });
});

describe('fallbacks', () => {
  it('raises CROP_IMPLAUSIBLE rather than printing a degenerate crop', () => {
    const profile: RoutingProfileRules = {
      profile: 'test',
      pageRules: [
        {
          id: 'always',
          name: 'always',
          when: { geometry: {} },
          then: {
            route: 'THERMAL',
            transform: { source: { unit: 'mm', xMm: 0, yMm: 0, widthMm: 5, heightMm: 5 } }
          }
        }
      ],
      fallback: { route: 'A4', onUnknown: 'route', byReason: { CROP_IMPLAUSIBLE: 'prompt' } }
    };

    const decision = decidedPage(page(), profile);
    expect(decision.fallback?.reason).toBe('CROP_IMPLAUSIBLE');
    expect(decision.fallback?.behaviour).toBe('prompt');
    expect(decision.fallback?.message).toContain('5.0x5.0mm');
  });

  it('raises LOW_CONFIDENCE when a rule matches below the profile threshold', () => {
    const decision = decidedPage(
      page({
        pageWidthMm: 210,
        pageHeightMm: 297,
        orientation: 'portrait',
        inkBox: { xMm: 12, yMm: 12, widthMm: 100, heightMm: 152, aspect: 1.52, coverage: 0.07 },
        barcodes: [
          { symbology: 'Code128', value: 'X123456789', xMm: 20, yMm: 120, widthMm: 60, heightMm: 12 }
        ]
      })
    );

    expect(decision.ruleId).toBe('generic-label-region');
    expect(decision.route).toBe('THERMAL');
    expect(decision.confidence).toBe(0.6);
    expect(decision.fallback?.reason).toBe('LOW_CONFIDENCE');
    expect(decision.fallback?.behaviour).toBe('prompt');
  });

  it('raises NO_THERMAL_CANDIDATE when a document that should carry a label has none', () => {
    const profile: RoutingProfileRules = {
      ...ONE_CLICK_PRINT_PROFILE,
      expectations: { thermalPagesPerDocument: { min: 1 } }
    };

    const invoice = page({
      pageWidthMm: 210,
      pageHeightMm: 297,
      orientation: 'portrait',
      text: 'Sales Invoice',
      inkBox: { xMm: 10, yMm: 16, widthMm: 190, heightMm: 234, aspect: 1.23, coverage: 0.06 }
    });

    const result = evaluateDocument(profile, document([invoice]));
    expect(result.status).toBe('decided');
    if (result.status === 'decided') {
      expect(result.document.fallback?.reason).toBe('NO_THERMAL_CANDIDATE');
      expect(result.document.fallback?.behaviour).toBe('prompt');
    }
  });

  it('raises AMBIGUOUS when more pages qualify than the profile expects', () => {
    const profile: RoutingProfileRules = {
      ...ONE_CLICK_PRINT_PROFILE,
      expectations: { thermalPagesPerDocument: { max: 1 } }
    };

    const label = page({
      inkBox: { xMm: 34.3, yMm: 19.3, widthMm: 101.1, heightMm: 149.9, aspect: 1.483, coverage: 0.064 }
    });

    const result = evaluateDocument(profile, document([label, { ...label, pageNumber: 2 }]));
    if (result.status === 'decided') {
      expect(result.document.fallback?.reason).toBe('AMBIGUOUS');
      expect(result.document.fallback?.candidatePages).toEqual([1, 2]);
    }
  });

  it('applies the profile default silently for ordinary document pages', () => {
    const decision = decidedPage(
      page({
        pageWidthMm: 210,
        pageHeightMm: 297,
        orientation: 'portrait',
        text: 'Return Note',
        inkBox: { xMm: 10, yMm: 15, widthMm: 190, heightMm: 253, aspect: 1.33, coverage: 0.07 }
      })
    );
    expect(decision.route).toBe('A4');
    expect(decision.fallback).toBeUndefined();
  });
});
