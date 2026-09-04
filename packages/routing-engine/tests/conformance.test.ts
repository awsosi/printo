import { readdirSync, readFileSync, existsSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import {
  BUILTIN_PROFILES,
  evaluateDocument,
  type ConformanceSuite,
  type RoutingProfileRules
} from '../src/index.js';

/**
 * Runs every shared conformance fixture through the TypeScript engine.
 *
 * The same files are run by the C# engine in
 * `clients/windows/Printo.Agent.Tests/ConformanceTests.cs`. When the two disagree the build
 * fails on both sides, which is the point: a workstation and the server that route the same
 * page differently is a defect nobody would otherwise notice until a parcel shipped with the
 * wrong label on it.
 */

const here = dirname(fileURLToPath(import.meta.url));
const fixtureRoot = resolve(here, '../../../tests/conformance');

function listFixtures(directory: string): string[] {
  const files: string[] = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...listFixtures(path));
    } else if (entry.name.endsWith('.json')) {
      files.push(path);
    }
  }
  return files.sort();
}

function resolveProfile(reference: string | RoutingProfileRules): RoutingProfileRules {
  if (typeof reference !== 'string') {
    return reference;
  }
  const prefix = 'builtin:';
  if (!reference.startsWith(prefix)) {
    throw new Error(`unsupported profile reference '${reference}'`);
  }
  const name = reference.slice(prefix.length);
  const profile = BUILTIN_PROFILES.find((candidate) => candidate.profile === name);
  if (!profile) {
    throw new Error(`no built-in profile named '${name}'`);
  }
  return profile;
}

const available = existsSync(fixtureRoot);
const files = available ? listFixtures(fixtureRoot) : [];

describe('conformance fixtures', () => {
  it('finds the shared fixture set', () => {
    expect(available, `expected fixtures at ${fixtureRoot}`).toBe(true);
    expect(files.length).toBeGreaterThan(0);
  });

  for (const file of files) {
    const suite = JSON.parse(readFileSync(file, 'utf8')) as ConformanceSuite;
    const label = relative(fixtureRoot, file).replace(/\\/g, '/');

    describe(label, () => {
      for (const fixture of suite.fixtures) {
        it(fixture.name, () => {
          const profile = resolveProfile(fixture.profile);
          const evaluation = evaluateDocument(profile, fixture.document);

          if (fixture.expectNeedsOcr && fixture.expectNeedsOcr.length > 0) {
            expect(evaluation.status, fixture.rationale).toBe('needs-features');
            if (evaluation.status !== 'needs-features') {
              return;
            }

            const actual = evaluation.ocr
              .map((request) => `${request.pageNumber}:${request.key}`)
              .sort();
            const expected = fixture.expectNeedsOcr
              .map((request) => `${request.pageNumber}:${request.key}`)
              .sort();
            expect(actual).toEqual(expected);

            for (const request of fixture.expectNeedsOcr) {
              if (request.ruleId === undefined) {
                continue;
              }
              const match = evaluation.ocr.find(
                (entry) => entry.pageNumber === request.pageNumber && entry.key === request.key
              );
              expect(match?.ruleId).toBe(request.ruleId);
            }
            return;
          }

          if (evaluation.status !== 'decided') {
            const missing = evaluation.ocr
              .map((request) => `p${request.pageNumber} ${request.key}`)
              .join(', ');
            throw new Error(`engine asked for OCR the fixture does not supply: ${missing}`);
          }

          const decision = evaluation.document;

          for (const want of fixture.expect?.pages ?? []) {
            const page = decision.pages.find((entry) => entry.pageNumber === want.pageNumber);
            expect(page, `no decision for page ${want.pageNumber}`).toBeDefined();
            if (!page) {
              continue;
            }

            if ('route' in want) {
              expect(page.route, `page ${want.pageNumber} rule ${page.ruleId ?? 'none'}`).toBe(want.route);
            }
            if ('ruleId' in want) {
              expect(page.ruleId).toBe(want.ruleId);
            }
            if ('confidence' in want) {
              expect(page.confidence).toBeCloseTo(want.confidence as number, 9);
            }
            if ('hold' in want) {
              expect(page.hold).toBe(want.hold);
            }
            if ('fallbackReason' in want) {
              expect(page.fallback?.reason ?? null).toBe(want.fallbackReason);
            }
            if ('carrier' in want) {
              expect(page.trace.carrier.carrier).toBe(want.carrier);
            }
            if ('ocrRectsUsed' in want) {
              expect([...page.trace.ocrRectsUsed].sort()).toEqual([...(want.ocrRectsUsed ?? [])].sort());
            }
          }

          const wantDocument = fixture.expect?.document;
          if (wantDocument) {
            if ('fallbackReason' in wantDocument) {
              expect(decision.fallback?.reason ?? null).toBe(wantDocument.fallbackReason);
            }
            if ('candidatePages' in wantDocument) {
              expect(decision.fallback?.candidatePages ?? []).toEqual(wantDocument.candidatePages);
            }
          }
        });
      }
    });
  }
});
