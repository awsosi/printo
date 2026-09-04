import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { BUILTIN_PROFILES, ONE_CLICK_PRINT_PROFILE, profileSlug } from '../src/index.js';

const here = dirname(fileURLToPath(import.meta.url));
const profilesDir = resolve(here, '../../../profiles');

describe('exported profiles', () => {
  /**
   * The Windows agent embeds `profiles/*.json` rather than carrying a hand-written C# copy of
   * the rules. If the export goes stale, the workstation and the server route differently —
   * the one failure mode nobody notices until a parcel ships with the courier copy on it.
   */
  it('match the TypeScript source', () => {
    for (const profile of BUILTIN_PROFILES) {
      const path = resolve(profilesDir, `${profileSlug(profile)}.json`);
      const exported = JSON.parse(readFileSync(path, 'utf8')) as unknown;
      expect(
        exported,
        `${path} is stale; run: npm run profiles:export -w @printo/routing-engine`
      ).toEqual(JSON.parse(JSON.stringify(profile)));
    }
  });

  it('keeps rule ids unique and stable', () => {
    // Rule ids are referenced by traces, by the review queue and by fallback analytics, so a
    // duplicate would silently merge two rules' history.
    for (const profile of BUILTIN_PROFILES) {
      const ids = profile.pageRules.map((rule) => rule.id);
      expect(new Set(ids).size, `duplicate rule id in ${profile.profile}`).toBe(ids.length);
    }
  });

  it('orders the OneClickPrint rules so OCR is the last resort', () => {
    // The laziness contract lives in the rule order, not in the engine: every rule that can
    // decide from text or geometry has to come before the one that forces a rasterize.
    const ids = ONE_CLICK_PRINT_PROFILE.pageRules.map((rule) => rule.id);
    const ocrIndex = ids.indexOf('dhl-waybill-sheet-ocr');
    expect(ocrIndex).toBeGreaterThan(ids.indexOf('dhl-waybill-sheet-text'));
    expect(ocrIndex).toBeGreaterThan(ids.indexOf('dhl-label-embedded-text'));
    expect(ocrIndex).toBeGreaterThan(ids.indexOf('fedex-label-embedded'));
    // ...and the geometry-only DHL rule must come after the OCR gate, or the courier sheet
    // would be claimed as a parcel label before anyone read it.
    expect(ids.indexOf('dhl-label-embedded')).toBeGreaterThan(ocrIndex);
  });

  it('declares a fallback route for every profile', () => {
    for (const profile of BUILTIN_PROFILES) {
      expect(profile.fallback.route.length).toBeGreaterThan(0);
    }
  });
});
