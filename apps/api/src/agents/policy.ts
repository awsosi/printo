import { DEFAULT_THERMAL_MEDIA, formatMedia, parseMedia, WAYBILL_HANDLINGS, type WaybillHandling } from '@printo/routing-engine';
import type { JsonObject } from '../types.js';

/**
 * The fleet policy: the operational settings this server hands every agent on its heartbeat,
 * and applies to what the worker routes itself.
 *
 * Stored as the settings an administrator actually chose, nothing more. A setting left unset
 * is absent from what the agents are sent, so each workstation falls back to its product
 * default and says so in its status window - rather than every machine reporting "set by the
 * server" for values nobody on the server ever touched.
 *
 * Layers, weakest first: product default, fleet policy, the agent's server-side overrides.
 * On the workstation, a value chosen on that machine or by Group Policy still wins over both;
 * the policy is a default for the fleet, not a lock.
 */

export type AgentLogLevel = 'debug' | 'information' | 'warning' | 'error' | 'critical';
export type PageOrder = 'auto' | 'firstPageFirst' | 'lastPageFirst';

export const LOG_LEVELS: readonly AgentLogLevel[] = ['debug', 'information', 'warning', 'error', 'critical'];
export const PAGE_ORDERS: readonly PageOrder[] = ['auto', 'firstPageFirst', 'lastPageFirst'];

export interface LoggingPolicy {
  fileEnabled: boolean;
  level: AgentLogLevel;
  maxFileSizeMb: number;
  maxFiles: number;
}

export interface RetentionPolicy {
  keepPrintedHours: number;
  keepHistoryDays: number;
  expireUnprintedDays: number;
  maxSpoolMb: number;
}

export interface PageOrderPolicy {
  a4: PageOrder;
  thermal: PageOrder;
}

/** A policy as stored: any subset of the settings, each group any subset of its fields. */
export interface FleetPolicyInput {
  waybillHandling?: WaybillHandling;
  thermalMedia?: string;
  logging?: Partial<LoggingPolicy>;
  retention?: Partial<RetentionPolicy>;
  pageOrder?: Partial<PageOrderPolicy>;
}

/** The product's own defaults, which the agent and the worker use when nothing is set. */
export const PRODUCT_DEFAULTS = {
  waybillHandling: 'route' as WaybillHandling,
  thermalMedia: formatMedia(DEFAULT_THERMAL_MEDIA),
  logging: { fileEnabled: false, level: 'information', maxFileSizeMb: 10, maxFiles: 5 } as LoggingPolicy,
  retention: { keepPrintedHours: 24, keepHistoryDays: 14, expireUnprintedDays: 30, maxSpoolMb: 2048 } as RetentionPolicy,
  pageOrder: { a4: 'auto', thermal: 'auto' } as PageOrderPolicy
};

export class PolicyError extends Error {
  constructor(
    public readonly path: string,
    detail: string
  ) {
    super(`${path}: ${detail}`);
    this.name = 'PolicyError';
  }
}

function isObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function integer(value: unknown, path: string, min: number, max: number): number {
  if (typeof value !== 'number' || !Number.isInteger(value) || value < min || value > max) {
    throw new PolicyError(path, `expected a whole number from ${min} to ${max}`);
  }
  return value;
}

function oneOf<T extends string>(value: unknown, path: string, allowed: readonly T[]): T {
  if (typeof value !== 'string' || !allowed.includes(value as T)) {
    throw new PolicyError(path, `expected one of ${allowed.join(', ')}`);
  }
  return value as T;
}

/**
 * Validates a policy from an administrator, keeping only what was given.
 *
 * Every value is checked against what the agent accepts, because a value the agent cannot
 * read is ignored by it - and an ignored "do not print waybills" prints them.
 */
export function parseFleetPolicy(value: unknown, path = 'policy'): FleetPolicyInput {
  if (value === null || value === undefined) {
    return {};
  }
  if (!isObject(value)) {
    throw new PolicyError(path, 'expected an object');
  }

  const known = new Set(['waybillHandling', 'thermalMedia', 'logging', 'retention', 'pageOrder']);
  for (const key of Object.keys(value)) {
    if (!known.has(key)) {
      throw new PolicyError(`${path}.${key}`, 'is not a fleet policy setting');
    }
  }

  const policy: FleetPolicyInput = {};

  if (value.waybillHandling !== undefined && value.waybillHandling !== null) {
    policy.waybillHandling = oneOf(value.waybillHandling, `${path}.waybillHandling`, WAYBILL_HANDLINGS);
  }

  if (value.thermalMedia !== undefined && value.thermalMedia !== null) {
    const media = typeof value.thermalMedia === 'string' ? parseMedia(value.thermalMedia) : null;
    if (!media) {
      throw new PolicyError(`${path}.thermalMedia`, 'expected a media size such as 100x210mm');
    }
    policy.thermalMedia = formatMedia(media);
  }

  if (value.logging !== undefined && value.logging !== null) {
    const group = value.logging;
    if (!isObject(group)) {
      throw new PolicyError(`${path}.logging`, 'expected an object');
    }
    const logging: Partial<LoggingPolicy> = {};
    if (group.fileEnabled !== undefined) {
      if (typeof group.fileEnabled !== 'boolean') {
        throw new PolicyError(`${path}.logging.fileEnabled`, 'expected true or false');
      }
      logging.fileEnabled = group.fileEnabled;
    }
    if (group.level !== undefined) {
      logging.level = oneOf(group.level, `${path}.logging.level`, LOG_LEVELS);
    }
    if (group.maxFileSizeMb !== undefined) {
      logging.maxFileSizeMb = integer(group.maxFileSizeMb, `${path}.logging.maxFileSizeMb`, 1, 1024);
    }
    if (group.maxFiles !== undefined) {
      logging.maxFiles = integer(group.maxFiles, `${path}.logging.maxFiles`, 1, 100);
    }
    if (Object.keys(logging).length > 0) {
      policy.logging = logging;
    }
  }

  if (value.retention !== undefined && value.retention !== null) {
    const group = value.retention;
    if (!isObject(group)) {
      throw new PolicyError(`${path}.retention`, 'expected an object');
    }
    const retention: Partial<RetentionPolicy> = {};
    if (group.keepPrintedHours !== undefined) {
      retention.keepPrintedHours = integer(group.keepPrintedHours, `${path}.retention.keepPrintedHours`, 0, 24 * 365);
    }
    if (group.keepHistoryDays !== undefined) {
      retention.keepHistoryDays = integer(group.keepHistoryDays, `${path}.retention.keepHistoryDays`, 1, 3650);
    }
    if (group.expireUnprintedDays !== undefined) {
      retention.expireUnprintedDays = integer(group.expireUnprintedDays, `${path}.retention.expireUnprintedDays`, 1, 3650);
    }
    if (group.maxSpoolMb !== undefined) {
      retention.maxSpoolMb = integer(group.maxSpoolMb, `${path}.retention.maxSpoolMb`, 50, 1024 * 1024);
    }
    if (Object.keys(retention).length > 0) {
      policy.retention = retention;
    }
  }

  if (value.pageOrder !== undefined && value.pageOrder !== null) {
    const group = value.pageOrder;
    if (!isObject(group)) {
      throw new PolicyError(`${path}.pageOrder`, 'expected an object');
    }
    const pageOrder: Partial<PageOrderPolicy> = {};
    if (group.a4 !== undefined) {
      pageOrder.a4 = oneOf(group.a4, `${path}.pageOrder.a4`, PAGE_ORDERS);
    }
    if (group.thermal !== undefined) {
      pageOrder.thermal = oneOf(group.thermal, `${path}.pageOrder.thermal`, PAGE_ORDERS);
    }
    if (Object.keys(pageOrder).length > 0) {
      policy.pageOrder = pageOrder;
    }
  }

  return policy;
}

/** Lays `overrides` over `base`, field by field within each group. */
export function mergePolicies(base: FleetPolicyInput, overrides: FleetPolicyInput): FleetPolicyInput {
  const merged: FleetPolicyInput = { ...base };
  if (overrides.waybillHandling !== undefined) {
    merged.waybillHandling = overrides.waybillHandling;
  }
  if (overrides.thermalMedia !== undefined) {
    merged.thermalMedia = overrides.thermalMedia;
  }
  if (overrides.logging) {
    merged.logging = { ...base.logging, ...overrides.logging };
  }
  if (overrides.retention) {
    merged.retention = { ...base.retention, ...overrides.retention };
  }
  if (overrides.pageOrder) {
    merged.pageOrder = { ...base.pageOrder, ...overrides.pageOrder };
  }
  return merged;
}

/**
 * What an agent is sent: the settings that are set, each group completed from the product
 * defaults - the agent takes a group whole - and unset settings left out.
 */
export function toAgentPolicy(policy: FleetPolicyInput, updatedAt: string | null): JsonObject {
  const wire: JsonObject = {};
  if (policy.waybillHandling !== undefined) {
    wire.waybillHandling = policy.waybillHandling;
  }
  if (policy.thermalMedia !== undefined) {
    wire.thermalMedia = policy.thermalMedia;
  }
  if (policy.logging && Object.keys(policy.logging).length > 0) {
    wire.logging = { ...PRODUCT_DEFAULTS.logging, ...policy.logging } as unknown as JsonObject;
  }
  if (policy.retention && Object.keys(policy.retention).length > 0) {
    wire.retention = { ...PRODUCT_DEFAULTS.retention, ...policy.retention } as unknown as JsonObject;
  }
  if (policy.pageOrder && Object.keys(policy.pageOrder).length > 0) {
    wire.pageOrder = { ...PRODUCT_DEFAULTS.pageOrder, ...policy.pageOrder } as unknown as JsonObject;
  }
  if (updatedAt) {
    wire.updatedAt = updatedAt;
  }
  return wire;
}

/** Every setting's value in force, product defaults included - what the console displays. */
export function effectivePolicy(policy: FleetPolicyInput) {
  return {
    waybillHandling: policy.waybillHandling ?? PRODUCT_DEFAULTS.waybillHandling,
    thermalMedia: policy.thermalMedia ?? PRODUCT_DEFAULTS.thermalMedia,
    logging: { ...PRODUCT_DEFAULTS.logging, ...policy.logging },
    retention: { ...PRODUCT_DEFAULTS.retention, ...policy.retention },
    pageOrder: { ...PRODUCT_DEFAULTS.pageOrder, ...policy.pageOrder }
  };
}
