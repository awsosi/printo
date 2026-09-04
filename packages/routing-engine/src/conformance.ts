/**
 * The conformance fixture format.
 *
 * A fixture is `(rule set + page features) -> expected decision`, expressed purely as data.
 * Both engine implementations run the same files from `tests/conformance/`, and a divergence
 * fails the build. This is the only thing that keeps the C# engine on the workstation and the
 * TypeScript engine on the server honest about each other as the rules evolve.
 */

import type { DocumentFeatures } from './features.js';
import type { FallbackReason, RoutingProfileRules } from './rules.js';

/** Expected outcome for one page. Fields left out are not asserted. */
export interface ExpectedPageOutcome {
  pageNumber: number;
  route?: string;
  ruleId?: string | null;
  confidence?: number;
  hold?: boolean;
  fallbackReason?: FallbackReason | null;
  /** Rectangles OCR was expected to be consulted for; asserts the laziness contract. */
  ocrRectsUsed?: string[];
  /** Carrier the resolver must report; `null` asserts "no carrier". */
  carrier?: string | null;
}

export interface ExpectedDocumentOutcome {
  fallbackReason?: FallbackReason | null;
  candidatePages?: number[];
}

export interface ConformanceFixture {
  name: string;
  /** Why this case exists. Shown when the fixture fails. */
  rationale?: string;
  /** `builtin:<profile name>` or an inline rule set. */
  profile: string | RoutingProfileRules;
  document: DocumentFeatures;
  /**
   * When set, the engine must stop and ask for exactly these OCR regions on the first pass.
   * Used to pin the lazy-evaluation contract, not just the final answer.
   */
  expectNeedsOcr?: Array<{ pageNumber: number; key: string; ruleId?: string }>;
  expect?: {
    pages?: ExpectedPageOutcome[];
    document?: ExpectedDocumentOutcome;
  };
}

export interface ConformanceSuite {
  version: number;
  fixtures: ConformanceFixture[];
}
