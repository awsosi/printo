import { describe, expect, it } from 'vitest';
import {
  effectivePolicy,
  mergePolicies,
  parseFleetPolicy,
  PolicyError,
  PRODUCT_DEFAULTS,
  toAgentPolicy
} from '../src/agents/policy.js';

/**
 * The fleet policy's rules, without a database: what is accepted, how layers merge, and what
 * an agent is sent. The round trip through Postgres is covered in agents-postgres.test.ts.
 */
describe('fleet policy', () => {
  it('keeps only what was set, normalising the media size', () => {
    expect(parseFleetPolicy({})).toEqual({});
    expect(parseFleetPolicy(null)).toEqual({});
    expect(parseFleetPolicy({ thermalMedia: '100 x 200', logging: { maxFiles: 3 } })).toEqual({
      thermalMedia: '100x200mm',
      logging: { maxFiles: 3 }
    });
  });

  it('refuses what an agent would ignore, naming the field', () => {
    const cases: Array<[unknown, string]> = [
      [{ waybillHandling: 'SKIP' }, 'policy.waybillHandling'],
      [{ thermalMedia: 42 }, 'policy.thermalMedia'],
      [{ logging: { fileEnabled: 'yes' } }, 'policy.logging.fileEnabled'],
      [{ logging: { maxFileSizeMb: 0 } }, 'policy.logging.maxFileSizeMb'],
      [{ retention: { keepHistoryDays: 1.5 } }, 'policy.retention.keepHistoryDays'],
      [{ pageOrder: { a4: 'reverse' } }, 'policy.pageOrder.a4'],
      [{ unknown: true }, 'policy.unknown'],
      ['all of it', 'policy']
    ];

    for (const [input, path] of cases) {
      let thrown: unknown;
      try {
        parseFleetPolicy(input);
      } catch (error) {
        thrown = error;
      }
      expect(thrown, JSON.stringify(input)).toBeInstanceOf(PolicyError);
      expect((thrown as PolicyError).path).toBe(path);
    }
  });

  it('lays an agent override over the fleet field by field', () => {
    const fleet = parseFleetPolicy({ waybillHandling: 'a4', logging: { fileEnabled: true, level: 'warning' } });
    const own = parseFleetPolicy({ logging: { level: 'debug' }, pageOrder: { thermal: 'firstPageFirst' } });

    expect(mergePolicies(fleet, own)).toEqual({
      waybillHandling: 'a4',
      logging: { fileEnabled: true, level: 'debug' },
      pageOrder: { thermal: 'firstPageFirst' }
    });
  });

  it('sends an agent only what is set, each group whole', () => {
    const wire = toAgentPolicy(parseFleetPolicy({ retention: { maxSpoolMb: 500 } }), '2026-09-24T10:00:00.000Z');

    expect(wire).toEqual({
      retention: { ...PRODUCT_DEFAULTS.retention, maxSpoolMb: 500 },
      updatedAt: '2026-09-24T10:00:00.000Z'
    });
  });

  it('reports the product defaults for anything unset', () => {
    const effective = effectivePolicy({});
    expect(effective.waybillHandling).toBe('route');
    expect(effective.thermalMedia).toBe('100x210mm');
    expect(effective.logging.fileEnabled).toBe(false);
    expect(effective.retention).toEqual(PRODUCT_DEFAULTS.retention);
  });
});
