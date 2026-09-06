/**
 * Turning a logged fallback into a rule.
 *
 * The fallback analytics exist to be driven to zero, and this is the mechanism: a page an
 * operator had to classify by hand carries, in its trace, the exact measurements the engine
 * made of it. A rule that would have matched those measurements is derivable, and derived
 * rules are what stop the same document interrupting somebody every morning.
 *
 * Three things this deliberately does not do:
 *
 *   - **It does not publish.** A generated rule is a proposal an administrator reads and
 *     edits. Pushing machine-written rules to thirty workstations on one click is how a fleet
 *     starts printing invoices on label stock at four in the afternoon.
 *   - **It does not generalise from one page.** The bounds come from what was measured, with
 *     an explicit tolerance, and the rule says so in its name. Inferring "all DHL labels look
 *     like this" from a single sample is how a rule ends up matching a waybill sheet too.
 *   - **It does not invent evidence.** If the carrier was not resolved, the proposed rule has
 *     no carrier predicate; if there was no ink box, no rule is proposed at all, because
 *     geometry is the only thing there is to key on.
 */

import type { DocumentDecision, PageDecision } from './trace.js';
import type { PageRule, RangeMm } from './rules.js';
import { ROUTE_THERMAL } from './rules.js';

export interface ProposeRuleOptions {
  /**
   * Fractional slack around each measured dimension. 0.1 means a rule that accepts a label
   * ten per cent larger or smaller than the sample.
   *
   * The default is deliberately loose. A rule fitted tightly to one page matches that page and
   * nothing else, which reads as a fix and is really a second fallback waiting to happen: the
   * next parcel's label is a millimetre different.
   */
  toleranceFraction?: number;
  /** Rule id. Defaults to one derived from the carrier and the reason. */
  id?: string;
  /** Confidence the rule asserts when it matches. */
  confidence?: number;
}

export interface RuleProposal {
  rule: PageRule;
  /** Human-readable justification, shown beside the rule in the review queue. */
  rationale: string[];
  /** The page the proposal was derived from. */
  pageNumber: number;
}

const DEFAULT_TOLERANCE = 0.1;

/** Rounds to one decimal, so a proposed rule reads like something a person wrote. */
function round(value: number): number {
  return Math.round(value * 10) / 10;
}

function around(value: number, tolerance: number): RangeMm {
  return { min: round(value * (1 - tolerance)), max: round(value * (1 + tolerance)) };
}

function slug(value: string): string {
  return value
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '');
}

/**
 * Derives a rule that would have routed the pages the operator chose.
 *
 * @param decision The engine's own decision, as uploaded with the fallback.
 * @param userSelection Pages the operator marked as labels.
 * @returns A proposal, or `null` when the trace holds nothing to key a rule on.
 */
export function proposeRuleFromFallback(
  decision: DocumentDecision,
  userSelection: number[],
  options: ProposeRuleOptions = {}
): RuleProposal | null {
  const tolerance = options.toleranceFraction ?? DEFAULT_TOLERANCE;

  const chosen = userSelection
    .map((pageNumber) => decision.pages.find((page) => page.pageNumber === pageNumber))
    .filter((page): page is PageDecision => page !== undefined);

  if (chosen.length === 0) {
    // The operator sent everything to A4. That is not a new rule - it is the profile default
    // already doing the right thing, and the fallback was raised for some other reason.
    return null;
  }

  // One page, not all of them: the pages an operator selects in one document are usually the
  // same label repeated, and averaging two different shapes would produce a rule matching
  // neither. The first is the one the picker pre-selected and they confirmed.
  const page = chosen[0];
  const geometry = page.trace.geometry;
  const ink = geometry.inkBox;

  if (!ink) {
    // A blank page, or one the extractor could not measure. Nothing to key on, and a rule
    // keyed on page size alone would match every sheet of A4 in the building.
    return null;
  }

  const rationale: string[] = [];
  const conditions: PageRule['when'][] = [];

  conditions.push({
    geometry: {
      orientation: geometry.orientation === 'landscape' ? 'landscape' : 'portrait',
      inkWidthMm: around(ink.widthMm, tolerance),
      inkHeightMm: around(ink.heightMm, tolerance),
      ...(geometry.inkAspect === undefined || geometry.inkAspect === null
        ? {}
        : { inkAspect: around(geometry.inkAspect, tolerance) })
    }
  });

  rationale.push(
    `The label region measured ${round(ink.widthMm)} x ${round(ink.heightMm)} mm on a ` +
      `${round(geometry.pageWidthMm)} x ${round(geometry.pageHeightMm)} mm ${geometry.orientation} page; ` +
      `the rule accepts +/-${Math.round(tolerance * 100)}%.`
  );

  const carrier = page.trace.carrier?.carrier ?? null;
  if (carrier) {
    conditions.push({ carrier: { is: carrier } });
    rationale.push(
      `The carrier resolved as ${carrier} at ${page.trace.carrier.confidence.toFixed(2)} confidence` +
        (page.trace.carrier.evidence.length > 0
          ? ` (${page.trace.carrier.evidence.map((entry) => entry.detail).join(', ')}).`
          : '.')
    );
  } else {
    rationale.push('No carrier was resolved, so the rule keys on geometry alone. Narrow it if it matches too much.');
  }

  const label = carrier ? `${carrier} label` : 'label';
  const id = options.id ?? `proposed-${slug(label)}-${slug(String(page.trace.geometry.inkAspect ?? 'x'))}`;

  rationale.push(
    'Derived from one page an operator classified by hand. Read it, narrow it if it would ' +
      'match documents it should not, and publish it deliberately.'
  );

  return {
    pageNumber: page.pageNumber,
    rationale,
    rule: {
      id,
      name: `${label} (proposed from a fallback on page ${page.pageNumber})`,
      when: conditions.length === 1 ? conditions[0] : { all: conditions },
      then: {
        route: ROUTE_THERMAL,
        confidence: options.confidence ?? 0.8,

        // The ink box with a millimetre of padding: the operator said this region is the
        // label, and the whole point is to crop it out of the carrier sheet.
        transform: { source: 'inkBox', padMm: 1, rotate: 'auto', fit: 'contain' }
      }
    }
  };
}
