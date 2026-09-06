import { describe, expect, it } from 'vitest';
import { evaluateDocument } from '../src/engine.js';
import { proposeRuleFromFallback } from '../src/propose.js';
import { parseBundlePayload } from '../src/wire.js';
import { ONE_CLICK_PRINT_PROFILE } from '../src/profiles.js';
import { ROUTE_A4, ROUTE_THERMAL, type RoutingProfileRules } from '../src/rules.js';
import type { DocumentDecision } from '../src/trace.js';
import type { DocumentFeatures, PageFeatures } from '../src/features.js';

/**
 * A fallback becoming a rule.
 *
 * The test that matters is the round trip: take a page an operator had to classify by hand,
 * derive a rule from its trace, and check the engine then routes that same page without asking.
 * A proposal that looks plausible but would not have matched is worse than none, because an
 * administrator would publish it and the prompt would keep appearing.
 */

function page(overrides: Partial<PageFeatures> = {}): PageFeatures {
  return {
    pageNumber: 1,
    pageCount: 1,
    pageWidthMm: 297,
    pageHeightMm: 210,
    orientation: 'landscape',
    rotation: 0,
    text: null,
    inkBox: { xMm: 12, yMm: 8, widthMm: 62, heightMm: 104, aspect: 1.68, coverage: 0.06 },
    barcodes: [],
    ...overrides
  };
}

function document(pages: PageFeatures[]): DocumentFeatures {
  return { fileName: 'OneClickPrint_TEST.pdf', pageCount: pages.length, pages };
}

/** Evaluates a document, requiring a decision rather than an OCR request. */
function decide(profile: RoutingProfileRules, features: DocumentFeatures): DocumentDecision {
  const evaluation = evaluateDocument(profile, features);
  if (evaluation.status !== 'decided') {
    throw new Error('the fixture should not need OCR');
  }
  return evaluation.document;
}

describe('proposing a rule from a fallback', () => {
  it('produces a rule that routes the page it was derived from', () => {
    // A small label-shaped region with no text and no barcode. Deliberately not the 4x6in
    // shape: at 101.6 x 152.4 mm `fedex-label-embedded` claims it outright, which is correct
    // behaviour and would make this a test of that rule rather than of the proposal.
    const features = document([page()]);
    const before = decide(ONE_CLICK_PRINT_PROFILE, features);
    expect(before.pages[0].route).toBe(ROUTE_A4);

    // The operator says page 1 is the label.
    const proposal = proposeRuleFromFallback(before, [1]);
    expect(proposal).not.toBeNull();

    const withRule: RoutingProfileRules = {
      ...ONE_CLICK_PRINT_PROFILE,
      pageRules: [proposal!.rule, ...ONE_CLICK_PRINT_PROFILE.pageRules]
    };

    const after = decide(withRule, features);
    expect(after.pages[0].route).toBe(ROUTE_THERMAL);
    expect(after.pages[0].ruleId).toBe(proposal!.rule.id);

    // And it crops, rather than printing the whole carrier sheet onto a 100x150 label.
    expect(after.pages[0].transform?.source).toBe('inkBox');
  });

  it('accepts the next label that is a few millimetres different', () => {
    const sample = document([page()]);
    const proposal = proposeRuleFromFallback(decide(ONE_CLICK_PRINT_PROFILE, sample), [1])!;

    const withRule: RoutingProfileRules = {
      ...ONE_CLICK_PRINT_PROFILE,
      pageRules: [proposal.rule, ...ONE_CLICK_PRINT_PROFILE.pageRules]
    };

    // A rule fitted tightly to one page reads as a fix and is really a second fallback waiting
    // to happen, so the tolerance is load-bearing rather than cosmetic.
    const nextParcel = document([
      page({
        inkBox: { xMm: 14, yMm: 9, widthMm: 64, heightMm: 101, aspect: 1.58, coverage: 0.058 }
      })
    ]);

    expect(decide(withRule, nextParcel).pages[0].route).toBe(ROUTE_THERMAL);
  });

  it('does not match a document of a different shape', () => {
    const sample = document([page()]);
    const proposal = proposeRuleFromFallback(decide(ONE_CLICK_PRINT_PROFILE, sample), [1])!;

    const withRule: RoutingProfileRules = {
      ...ONE_CLICK_PRINT_PROFILE,
      pageRules: [proposal.rule, ...ONE_CLICK_PRINT_PROFILE.pageRules]
    };

    // A full-page A4 invoice: same paper, entirely different ink. Must stay on A4, or the
    // proposal has generalised from one sample into a rule that prints invoices on labels.
    const invoice = document([
      page({
        pageWidthMm: 210,
        pageHeightMm: 297,
        orientation: 'portrait',
        text: 'Invoice 2026/09/1234\nTotal 412,90 EUR',
        inkBox: { xMm: 15, yMm: 15, widthMm: 180, heightMm: 240, aspect: 1.33, coverage: 0.21 }
      })
    ]);

    expect(decide(withRule, invoice).pages[0].route).toBe(ROUTE_A4);
  });

  it('keys on the carrier when one was resolved, and says so', () => {
    const features = document([
      page({
        text: 'MyDHL Express Worldwide  JD014600009012345678',
        barcodes: [
          {
            xMm: 20,
            yMm: 100,
            widthMm: 70,
            heightMm: 15,
            symbology: 'Code128',
            value: 'JD014600009012345678'
          }
        ]
      })
    ]);

    const proposal = proposeRuleFromFallback(decide(ONE_CLICK_PRINT_PROFILE, features), [1])!;

    expect(JSON.stringify(proposal.rule.when)).toContain('"carrier"');
    expect(proposal.rule.name).toContain('DHL');
    expect(proposal.rationale.join(' ')).toContain('DHL');

    // The rationale has to be readable by whoever decides whether to publish it.
    expect(proposal.rationale.join(' ')).toContain('publish it deliberately');
  });

  it('refuses to invent a rule when there is nothing to key on', () => {
    // Nothing selected: the operator sent the whole document to A4, which is the profile
    // default already working. There is no rule to write.
    expect(proposeRuleFromFallback(decide(ONE_CLICK_PRINT_PROFILE, document([page()])), [])).toBeNull();

    // A blank page. A rule keyed on page size alone would match every sheet in the building.
    const blank = document([page({ inkBox: null })]);
    expect(proposeRuleFromFallback(decide(ONE_CLICK_PRINT_PROFILE, blank), [1])).toBeNull();
  });

  it('produces a rule the bundle validator accepts', () => {
    const proposal = proposeRuleFromFallback(
      decide(ONE_CLICK_PRINT_PROFILE, document([page()])),
      [1]
    )!;

    // The proposal goes into a bundle that is validated before it reaches a single machine, so
    // a generated rule that the validator rejects would be a dead end in the console.
    const bundle = {
      schemaVersion: 1,
      profiles: [
        {
          ...JSON.parse(JSON.stringify(ONE_CLICK_PRINT_PROFILE)),
          pageRules: [proposal.rule, ...JSON.parse(JSON.stringify(ONE_CLICK_PRINT_PROFILE.pageRules))]
        }
      ]
    };

    expect(() => parseBundlePayload(bundle)).not.toThrow();
  });
});
