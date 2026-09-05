import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { BUILTIN_PROFILES } from '../src/profiles.js';
import {
  BUNDLE_SCHEMA_VERSION,
  parseBundlePayload,
  parseDocumentFeatures,
  parsePredicate,
  WireFormatError
} from '../src/wire.js';
import { evaluateDocument, matchProfile } from '../src/engine.js';
import type { ConformanceSuite } from '../src/conformance.js';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');

/** A bundle carrying the profiles the product ships with. */
function bundle(): unknown {
  return {
    schemaVersion: BUNDLE_SCHEMA_VERSION,
    profiles: JSON.parse(JSON.stringify(BUILTIN_PROFILES))
  };
}

function expectRejected(value: unknown, path: string, detail?: string): void {
  let thrown: unknown;
  try {
    parseBundlePayload(value);
  } catch (error) {
    thrown = error;
  }

  expect(thrown, 'expected the bundle to be rejected').toBeInstanceOf(WireFormatError);
  expect((thrown as WireFormatError).path).toBe(path);
  if (detail) {
    expect((thrown as WireFormatError).message).toContain(detail);
  }
}

describe('rule bundle validation', () => {
  it('accepts the profiles the product ships with, unchanged', () => {
    const parsed = parseBundlePayload(bundle());

    // Round-tripping the shipped profiles is the load-bearing case: the exporter writes them
    // to `profiles/`, the agent embeds that file, and the validator has to agree with both.
    expect(parsed.profiles).toEqual(BUILTIN_PROFILES);
  });

  it('rejects a predicate neither engine could execute', () => {
    const payload = bundle() as { profiles: Array<Record<string, unknown>> };
    (payload.profiles[0].pageRules as Array<Record<string, unknown>>)[0].when = { colour: { is: 'red' } };

    expectRejected(payload, 'bundle.profiles[0].pageRules[0].when.colour', 'unknown predicate');
  });

  it('rejects a predicate carrying two keys', () => {
    const payload = bundle() as { profiles: Array<Record<string, unknown>> };
    (payload.profiles[0].pageRules as Array<Record<string, unknown>>)[0].when = {
      text: { contains: 'x' },
      geometry: { orientation: 'portrait' }
    };

    // The C# engine dispatches on the single known key. Honouring the first here would make
    // one rule mean two different things on the two sides.
    expectRejected(payload, 'bundle.profiles[0].pageRules[0].when', 'exactly one is allowed');
  });

  it('rejects an empty composite, which silently matches everything or nothing', () => {
    const payload = bundle() as { profiles: Array<Record<string, unknown>> };
    (payload.profiles[0].pageRules as Array<Record<string, unknown>>)[0].when = { all: [] };

    expectRejected(payload, 'bundle.profiles[0].pageRules[0].when.all', 'at least one predicate');
  });

  it('rejects an inverted range, which reads as an active rule but matches nothing', () => {
    const payload = bundle() as { profiles: Array<Record<string, unknown>> };
    (payload.profiles[0].pageRules as Array<Record<string, unknown>>)[0].when = {
      geometry: { inkAspect: { min: 2, max: 1 } }
    };

    expectRejected(
      payload,
      'bundle.profiles[0].pageRules[0].when.geometry.inkAspect',
      'min 2 is greater than max 1'
    );
  });

  it('rejects a regular expression that does not compile', () => {
    const payload = bundle() as { profiles: Array<Record<string, unknown>> };
    (payload.profiles[0].pageRules as Array<Record<string, unknown>>)[0].when = {
      text: { matches: 'Waybill(' }
    };

    expectRejected(payload, 'bundle.profiles[0].pageRules[0].when.text.matches', 'not a valid regular expression');
  });

  it('rejects duplicate rule ids, which make every trace ambiguous', () => {
    const payload = bundle() as { profiles: Array<{ pageRules: Array<Record<string, unknown>> }> };
    const rules = payload.profiles[0].pageRules;
    rules.push({ ...rules[0] });

    expectRejected(payload, 'bundle.profiles[0].pageRules', 'duplicate rule id');
  });

  it('rejects an unknown fallback behaviour and an unknown named rectangle', () => {
    const behaviour = bundle() as { profiles: Array<{ fallback: Record<string, unknown> }> };
    behaviour.profiles[0].fallback.onUnknown = 'ignore';
    expectRejected(behaviour, 'bundle.profiles[0].fallback.onUnknown');

    const rect = bundle() as { profiles: Array<Record<string, unknown>> };
    (rect.profiles[0].pageRules as Array<Record<string, unknown>>)[0].when = {
      ocr: { rect: 'labelArea', contains: 'x' }
    };
    expectRejected(rect, 'bundle.profiles[0].pageRules[0].when.ocr.rect', 'unknown named rectangle');
  });

  it('rejects a bundle with no profiles or the wrong schema version', () => {
    expectRejected({ schemaVersion: 1, profiles: [] }, 'bundle.profiles', 'routes nothing');
    expectRejected({ schemaVersion: 2, profiles: [{}] }, 'bundle.schemaVersion');
  });

  it('validates carrier signatures, compiling every pattern but the symbology name', () => {
    const withSignatures = {
      ...(bundle() as object),
      carrierSignatures: [
        {
          carrier: 'UPS',
          signals: [
            { source: 'barcodeSymbology', pattern: 'MaxiCode', weight: 0.7, label: 'MaxiCode' },
            { source: 'text', pattern: '\\bUPS\\b', weight: 0.35, label: 'UPS' }
          ]
        }
      ]
    };

    // `MaxiCode` is compared exactly, not as a regex, so it must not be required to compile
    // as one - and equally, a text pattern must.
    expect(parseBundlePayload(withSignatures).carrierSignatures).toHaveLength(1);

    const broken = JSON.parse(JSON.stringify(withSignatures));
    broken.carrierSignatures[0].signals[1].pattern = '\\b(UPS';
    expectRejected(broken, 'bundle.carrierSignatures[0].signals[1].pattern');

    const overweight = JSON.parse(JSON.stringify(withSignatures));
    overweight.carrierSignatures[0].signals[0].weight = 5;
    expectRejected(overweight, 'bundle.carrierSignatures[0].signals[0].weight');
  });

  it('reports the path of a nested failure so an admin can find the rule', () => {
    const payload = bundle() as { profiles: Array<Record<string, unknown>> };
    (payload.profiles[0].pageRules as Array<Record<string, unknown>>)[0].when = {
      all: [{ geometry: { orientation: 'portrait' } }, { any: [{ text: {} }] }]
    };

    expectRejected(
      payload,
      'bundle.profiles[0].pageRules[0].when.all[1].any[0].text',
      'needs either contains or matches'
    );
  });

  it('parses every predicate form the schema defines', () => {
    const forms: unknown[] = [
      { text: { contains: 'x', withinRect: 'inkBox' } },
      { ocr: { rect: { unit: 'pageFraction', x: 0, y: 0, w: 1, h: 0.5 }, matches: 'WAYBILL' } },
      { barcode: { symbology: ['Code128'], minCount: 1, rect: 'barcodeCluster' } },
      { image: { template: 'dhl-logo', threshold: 0.8 } },
      { geometry: { pageIsLabelStock: true, inkCoverage: { min: 0.01 } } },
      { carrier: { in: ['DHL', 'UPS'], minConfidence: 0.5 } },
      { pageIndex: { is: 'last' } },
      { not: { text: { contains: 'copy' } } }
    ];

    for (const [index, form] of forms.entries()) {
      expect(parsePredicate(form, `p${index}`), `form ${index}`).toEqual(form);
    }
  });
});

describe('document feature validation', () => {
  const suite: ConformanceSuite = JSON.parse(
    readFileSync(resolve(repoRoot, 'tests/conformance/lazy-ocr-and-fallbacks.json'), 'utf8')
  );

  it('accepts the conformance documents and decides on them identically', () => {
    for (const fixture of suite.fixtures) {
      const features = parseDocumentFeatures(JSON.parse(JSON.stringify(fixture.document)));
      const profile = matchProfile(BUILTIN_PROFILES, features);
      expect(profile, fixture.name).not.toBeNull();

      // Parsing must be lossless where it matters: the same fixture has to reach the same
      // verdict whether the engine gets it directly or off the wire.
      expect(evaluateDocument(profile!, features), fixture.name).toEqual(
        evaluateDocument(profile!, fixture.document)
      );
    }
  });

  it('names the page and field a malformed document failed on', () => {
    expect(() =>
      parseDocumentFeatures({
        fileName: 'x.pdf',
        pages: [
          {
            pageNumber: 1,
            pageCount: 1,
            pageWidthMm: 210,
            pageHeightMm: 297,
            orientation: 'portrait',
            text: null,
            inkBox: { xMm: 0, yMm: 0, widthMm: 10, heightMm: 10 },
            barcodes: []
          }
        ]
      })
    ).toThrowError(/features\.pages\[0\]\.inkBox\.aspect/);

    expect(() => parseDocumentFeatures({ fileName: 'x.pdf', pages: [{}] })).toThrowError(
      /features\.pages\[0\]\.orientation/
    );

    expect(() => parseDocumentFeatures({ pages: [] })).toThrowError(/features\.fileName/);
  });

  it('defaults the page count and tolerates a document with no optional arrays', () => {
    const parsed = parseDocumentFeatures({
      fileName: 'x.pdf',
      pages: [
        {
          pageNumber: 1,
          pageCount: 1,
          pageWidthMm: 210,
          pageHeightMm: 297,
          orientation: 'portrait',
          text: 'hello'
        }
      ]
    });

    expect(parsed.pageCount).toBe(1);
    expect(parsed.pages[0].inkBox).toBeNull();
    expect(parsed.pages[0].barcodes).toEqual([]);
    expect(parsed.pages[0].rotation).toBe(0);
  });
});
