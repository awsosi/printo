/**
 * The over-the-wire contract between the agent and the server.
 *
 * Two things cross the network in a shape the engine has to execute: the **rule bundle** the
 * server publishes to every workstation, and the **document features** an agent posts when it
 * asks the server to decide. Both arrive as untrusted JSON, and both are parsed here rather
 * than cast.
 *
 * The validation is deliberately structural and complete rather than a smoke test. A bundle
 * with a predicate key neither engine knows would be accepted by `JSON.parse`, published to
 * twenty or thirty machines, and then fail at print time on every one of them - the C# side
 * throws on an unknown predicate key by design. Catching it at publish time turns a
 * fleet-wide outage into a 400 on one admin request.
 */

import type { CarrierSignal, CarrierSignalSource, CarrierSignatureSet } from './carrier.js';
import type {
  DetectedBarcode,
  DocumentFeatures,
  InkBox,
  OcrRegion,
  PageFeatures,
  RectMm,
  TemplateMatch,
  TextLine
} from './features.js';
import type {
  FallbackBehaviour,
  GeometryPredicate,
  PageRule,
  Predicate,
  RectSpec,
  RoutingProfileRules,
  TransformSpec
} from './rules.js';

/** Raised when something crossing the wire does not match the schema. Carries the JSON path. */
export class WireFormatError extends Error {
  constructor(
    readonly path: string,
    detail: string
  ) {
    super(`${path}: ${detail}`);
    this.name = 'WireFormatError';
  }
}

export const BUNDLE_SCHEMA_VERSION = 1;

/**
 * What an agent downloads and executes.
 *
 * `carrierSignatures` is optional and overrides the engine's built-in table when present, so
 * a new courier can be taught to the fleet by publishing a bundle rather than by shipping a
 * new agent build.
 */
export interface RuleBundlePayload {
  schemaVersion: number;
  profiles: RoutingProfileRules[];
  carrierSignatures?: CarrierSignatureSet[];
  /** ISO-8601. Informational: the version number is what agents compare. */
  generatedAt?: string;
}

const PREDICATE_KEYS = [
  'all',
  'any',
  'not',
  'text',
  'ocr',
  'barcode',
  'image',
  'geometry',
  'carrier',
  'pageIndex'
] as const;

const FALLBACK_BEHAVIOURS: FallbackBehaviour[] = ['prompt', 'route', 'hold'];

const NAMED_RECTS = ['page', 'inkBox', 'barcodeCluster'];

const GEOMETRY_RANGES = [
  'pageWidthMm',
  'pageHeightMm',
  'inkWidthMm',
  'inkHeightMm',
  'inkXMm',
  'inkYMm',
  'inkAspect',
  'inkCoverage'
] as const;

function isObject(value: unknown, path: string): asserts value is Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new WireFormatError(path, 'expected an object');
  }
}

function isArray(value: unknown, path: string): asserts value is unknown[] {
  if (!Array.isArray(value)) {
    throw new WireFormatError(path, 'expected an array');
  }
}

function requireString(value: unknown, path: string): string {
  if (typeof value !== 'string' || value.length === 0) {
    throw new WireFormatError(path, 'expected a non-empty string');
  }
  return value;
}

function requireNumber(value: unknown, path: string): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new WireFormatError(path, 'expected a finite number');
  }
  return value;
}

function optionalString(value: unknown, path: string): string | undefined {
  return value === undefined || value === null ? undefined : requireString(value, path);
}

function optionalNumber(value: unknown, path: string): number | undefined {
  return value === undefined || value === null ? undefined : requireNumber(value, path);
}

function optionalBoolean(value: unknown, path: string): boolean | undefined {
  if (value === undefined || value === null) {
    return undefined;
  }
  if (typeof value !== 'boolean') {
    throw new WireFormatError(path, 'expected a boolean');
  }
  return value;
}

/** Compiles a rule's regular expression, so a bad one fails at publish rather than at print. */
function requireRegExp(value: unknown, path: string): string {
  const source = requireString(value, path);
  try {
    RegExp(source);
  } catch (error) {
    throw new WireFormatError(path, `is not a valid regular expression: ${(error as Error).message}`);
  }
  return source;
}

function parseRectSpec(value: unknown, path: string): RectSpec {
  if (typeof value === 'string') {
    if (!NAMED_RECTS.includes(value)) {
      throw new WireFormatError(path, `unknown named rectangle '${value}'`);
    }
    return value as RectSpec;
  }

  isObject(value, path);
  if (value.unit === 'mm') {
    return {
      unit: 'mm',
      xMm: requireNumber(value.xMm, `${path}.xMm`),
      yMm: requireNumber(value.yMm, `${path}.yMm`),
      widthMm: requireNumber(value.widthMm, `${path}.widthMm`),
      heightMm: requireNumber(value.heightMm, `${path}.heightMm`)
    };
  }

  if (value.unit === 'pageFraction') {
    return {
      unit: 'pageFraction',
      x: requireNumber(value.x, `${path}.x`),
      y: requireNumber(value.y, `${path}.y`),
      w: requireNumber(value.w, `${path}.w`),
      h: requireNumber(value.h, `${path}.h`)
    };
  }

  throw new WireFormatError(`${path}.unit`, "expected 'mm' or 'pageFraction'");
}

function parseRange(value: unknown, path: string): { min?: number; max?: number } {
  isObject(value, path);
  const range = {
    min: optionalNumber(value.min, `${path}.min`),
    max: optionalNumber(value.max, `${path}.max`)
  };

  if (range.min !== undefined && range.max !== undefined && range.min > range.max) {
    // An inverted range matches nothing, which in a routing rule reads as "this rule is
    // disabled" while looking like it is active. Almost always a typo.
    throw new WireFormatError(path, `min ${range.min} is greater than max ${range.max}`);
  }

  return range;
}

/**
 * Parses one predicate, rejecting unknown and ambiguous forms.
 *
 * A predicate object carries exactly one key. Two keys is not a union the engines can
 * evaluate - C# dispatches on the single known key - and silently honouring the first would
 * make the rule mean different things in the two implementations.
 */
export function parsePredicate(value: unknown, path: string): Predicate {
  isObject(value, path);

  const keys = Object.keys(value).filter((key) => value[key] !== undefined);
  if (keys.length !== 1) {
    throw new WireFormatError(
      path,
      keys.length === 0
        ? 'predicate is empty'
        : `predicate carries ${keys.length} keys (${keys.join(', ')}); exactly one is allowed`
    );
  }

  const key = keys[0];
  if (!(PREDICATE_KEYS as readonly string[]).includes(key)) {
    throw new WireFormatError(
      `${path}.${key}`,
      `unknown predicate; expected one of ${PREDICATE_KEYS.join(', ')}`
    );
  }

  const body = value[key];

  switch (key) {
    case 'all':
    case 'any': {
      isArray(body, `${path}.${key}`);
      if (body.length === 0) {
        // `all: []` is vacuously true and `any: []` vacuously false. Both are almost certainly
        // an editor bug, and both route real parcels.
        throw new WireFormatError(`${path}.${key}`, 'must contain at least one predicate');
      }
      const children = body.map((child, index) => parsePredicate(child, `${path}.${key}[${index}]`));
      return key === 'all' ? { all: children } : { any: children };
    }

    case 'not':
      return { not: parsePredicate(body, `${path}.not`) };

    case 'text': {
      isObject(body, `${path}.text`);
      if (body.contains === undefined && body.matches === undefined) {
        throw new WireFormatError(`${path}.text`, 'needs either contains or matches');
      }
      return {
        text: {
          contains: optionalString(body.contains, `${path}.text.contains`),
          matches:
            body.matches === undefined || body.matches === null
              ? undefined
              : requireRegExp(body.matches, `${path}.text.matches`),
          withinRect:
            body.withinRect === undefined || body.withinRect === null
              ? undefined
              : parseRectSpec(body.withinRect, `${path}.text.withinRect`),
          caseSensitive: optionalBoolean(body.caseSensitive, `${path}.text.caseSensitive`)
        }
      };
    }

    case 'ocr': {
      isObject(body, `${path}.ocr`);
      if (body.contains === undefined && body.matches === undefined) {
        throw new WireFormatError(`${path}.ocr`, 'needs either contains or matches');
      }
      return {
        ocr: {
          rect: parseRectSpec(body.rect, `${path}.ocr.rect`),
          contains: optionalString(body.contains, `${path}.ocr.contains`),
          matches:
            body.matches === undefined || body.matches === null
              ? undefined
              : requireRegExp(body.matches, `${path}.ocr.matches`),
          caseSensitive: optionalBoolean(body.caseSensitive, `${path}.ocr.caseSensitive`),
          ignoreSpacing: optionalBoolean(body.ignoreSpacing, `${path}.ocr.ignoreSpacing`)
        }
      };
    }

    case 'barcode': {
      isObject(body, `${path}.barcode`);
      let symbology: string[] | undefined;
      if (body.symbology !== undefined && body.symbology !== null) {
        isArray(body.symbology, `${path}.barcode.symbology`);
        symbology = body.symbology.map((entry, index) =>
          requireString(entry, `${path}.barcode.symbology[${index}]`)
        );
      }
      return {
        barcode: {
          symbology,
          valueMatches:
            body.valueMatches === undefined || body.valueMatches === null
              ? undefined
              : requireRegExp(body.valueMatches, `${path}.barcode.valueMatches`),
          valueContains: optionalString(body.valueContains, `${path}.barcode.valueContains`),
          minCount: optionalNumber(body.minCount, `${path}.barcode.minCount`),
          maxCount: optionalNumber(body.maxCount, `${path}.barcode.maxCount`),
          rect:
            body.rect === undefined || body.rect === null
              ? undefined
              : parseRectSpec(body.rect, `${path}.barcode.rect`)
        }
      };
    }

    case 'image': {
      isObject(body, `${path}.image`);
      return {
        image: {
          template: requireString(body.template, `${path}.image.template`),
          threshold: requireNumber(body.threshold, `${path}.image.threshold`),
          searchRect:
            body.searchRect === undefined || body.searchRect === null
              ? undefined
              : parseRectSpec(body.searchRect, `${path}.image.searchRect`)
        }
      };
    }

    case 'geometry': {
      isObject(body, `${path}.geometry`);
      if (
        body.orientation !== undefined &&
        body.orientation !== null &&
        body.orientation !== 'portrait' &&
        body.orientation !== 'landscape'
      ) {
        throw new WireFormatError(`${path}.geometry.orientation`, "expected 'portrait' or 'landscape'");
      }

      const geometry: GeometryPredicate = {
        orientation: (body.orientation ?? undefined) as GeometryPredicate['orientation'],
        pageIsLabelStock: optionalBoolean(body.pageIsLabelStock, `${path}.geometry.pageIsLabelStock`)
      };

      for (const field of GEOMETRY_RANGES) {
        const range = body[field];
        if (range !== undefined && range !== null) {
          geometry[field] = parseRange(range, `${path}.geometry.${field}`);
        }
      }

      return { geometry };
    }

    case 'carrier': {
      isObject(body, `${path}.carrier`);
      let carriers: string[] | undefined;
      if (body.in !== undefined && body.in !== null) {
        isArray(body.in, `${path}.carrier.in`);
        carriers = body.in.map((entry, index) => requireString(entry, `${path}.carrier.in[${index}]`));
      }
      return {
        carrier: {
          is: optionalString(body.is, `${path}.carrier.is`),
          in: carriers,
          minConfidence: optionalNumber(body.minConfidence, `${path}.carrier.minConfidence`)
        }
      };
    }

    default: {
      isObject(body, `${path}.pageIndex`);
      if (body.is !== undefined && body.is !== null && body.is !== 'first' && body.is !== 'last') {
        throw new WireFormatError(`${path}.pageIndex.is`, "expected 'first' or 'last'");
      }
      return {
        pageIndex: {
          is: (body.is ?? undefined) as 'first' | 'last' | undefined,
          nth: optionalNumber(body.nth, `${path}.pageIndex.nth`),
          range:
            body.range === undefined || body.range === null
              ? undefined
              : parseRange(body.range, `${path}.pageIndex.range`)
        }
      };
    }
  }
}

function parseTransform(value: unknown, path: string): TransformSpec {
  isObject(value, path);

  const rotate = value.rotate;
  if (
    rotate !== undefined &&
    rotate !== null &&
    rotate !== 'auto' &&
    rotate !== 0 &&
    rotate !== 90 &&
    rotate !== 180 &&
    rotate !== 270
  ) {
    throw new WireFormatError(`${path}.rotate`, "expected 'auto', 0, 90, 180 or 270");
  }

  const fit = value.fit;
  if (
    fit !== undefined &&
    fit !== null &&
    fit !== 'contain' &&
    fit !== 'cover' &&
    fit !== 'actual' &&
    fit !== 'stretch'
  ) {
    throw new WireFormatError(`${path}.fit`, "expected 'contain', 'cover', 'actual' or 'stretch'");
  }

  const colorMode = value.colorMode;
  if (colorMode !== undefined && colorMode !== null && colorMode !== 'mono' && colorMode !== 'color') {
    throw new WireFormatError(`${path}.colorMode`, "expected 'mono' or 'color'");
  }

  const duplex = value.duplex;
  if (
    duplex !== undefined &&
    duplex !== null &&
    duplex !== 'simplex' &&
    duplex !== 'long-edge' &&
    duplex !== 'short-edge'
  ) {
    throw new WireFormatError(`${path}.duplex`, "expected 'simplex', 'long-edge' or 'short-edge'");
  }

  return {
    source:
      value.source === undefined || value.source === null
        ? undefined
        : parseRectSpec(value.source, `${path}.source`),
    padMm: optionalNumber(value.padMm, `${path}.padMm`),
    rotate: (rotate ?? undefined) as TransformSpec['rotate'],
    fit: (fit ?? undefined) as TransformSpec['fit'],
    media: optionalString(value.media, `${path}.media`),
    zoomPercent: optionalNumber(value.zoomPercent, `${path}.zoomPercent`),
    panXMm: optionalNumber(value.panXMm, `${path}.panXMm`),
    panYMm: optionalNumber(value.panYMm, `${path}.panYMm`),
    copies: optionalNumber(value.copies, `${path}.copies`),
    colorMode: (colorMode ?? undefined) as TransformSpec['colorMode'],
    duplex: (duplex ?? undefined) as TransformSpec['duplex'],
    tray: optionalString(value.tray, `${path}.tray`)
  };
}

function parsePageRule(value: unknown, path: string): PageRule {
  isObject(value, path);

  const action = value.then;
  isObject(action, `${path}.then`);

  return {
    id: requireString(value.id, `${path}.id`),
    name: requireString(value.name, `${path}.name`),
    when: parsePredicate(value.when, `${path}.when`),
    then: {
      route: optionalString(action.route, `${path}.then.route`),
      transform:
        action.transform === undefined || action.transform === null
          ? undefined
          : parseTransform(action.transform, `${path}.then.transform`),
      copies: optionalNumber(action.copies, `${path}.then.copies`),
      hold: optionalBoolean(action.hold, `${path}.then.hold`),
      confidence: optionalNumber(action.confidence, `${path}.then.confidence`),
      stop: optionalBoolean(action.stop, `${path}.then.stop`)
    },
    enabled: optionalBoolean(value.enabled, `${path}.enabled`)
  };
}

/** Parses one profile, rejecting anything either engine could not execute. */
export function parseProfile(value: unknown, path: string): RoutingProfileRules {
  isObject(value, path);

  const fallback = value.fallback;
  isObject(fallback, `${path}.fallback`);

  const onUnknown = requireString(fallback.onUnknown, `${path}.fallback.onUnknown`);
  if (!FALLBACK_BEHAVIOURS.includes(onUnknown as FallbackBehaviour)) {
    throw new WireFormatError(
      `${path}.fallback.onUnknown`,
      `expected one of ${FALLBACK_BEHAVIOURS.join(', ')}`
    );
  }

  const byReason: Record<string, FallbackBehaviour> = {};
  if (fallback.byReason !== undefined && fallback.byReason !== null) {
    isObject(fallback.byReason, `${path}.fallback.byReason`);
    for (const [reason, behaviour] of Object.entries(fallback.byReason)) {
      if (!FALLBACK_BEHAVIOURS.includes(behaviour as FallbackBehaviour)) {
        throw new WireFormatError(
          `${path}.fallback.byReason.${reason}`,
          `expected one of ${FALLBACK_BEHAVIOURS.join(', ')}`
        );
      }
      byReason[reason] = behaviour as FallbackBehaviour;
    }
  }

  isArray(value.pageRules, `${path}.pageRules`);
  const pageRules = value.pageRules.map((rule, index) =>
    parsePageRule(rule, `${path}.pageRules[${index}]`)
  );

  const seen = new Set<string>();
  for (const rule of pageRules) {
    if (seen.has(rule.id)) {
      // Rule ids are how a trace, a review-queue entry and a proposed fix refer to a rule.
      // Two rules sharing one makes every one of those ambiguous.
      throw new WireFormatError(`${path}.pageRules`, `duplicate rule id '${rule.id}'`);
    }
    seen.add(rule.id);
  }

  const match = value.match;
  let profileMatch: RoutingProfileRules['match'];
  if (match !== undefined && match !== null) {
    isObject(match, `${path}.match`);
    profileMatch = {
      filenameMask: optionalString(match.filenameMask, `${path}.match.filenameMask`),
      sourceApp: optionalString(match.sourceApp, `${path}.match.sourceApp`),
      minPages: optionalNumber(match.minPages, `${path}.match.minPages`),
      maxPages: optionalNumber(match.maxPages, `${path}.match.maxPages`)
    };
  }

  let expectations: RoutingProfileRules['expectations'];
  const declared = value.expectations;
  if (declared !== undefined && declared !== null) {
    isObject(declared, `${path}.expectations`);
    if (
      declared.thermalPagesPerDocument !== undefined &&
      declared.thermalPagesPerDocument !== null
    ) {
      expectations = {
        thermalPagesPerDocument: parseRange(
          declared.thermalPagesPerDocument,
          `${path}.expectations.thermalPagesPerDocument`
        )
      };
    }
  }

  return {
    profile: requireString(value.profile, `${path}.profile`),
    version: optionalNumber(value.version, `${path}.version`),
    match: profileMatch,
    confidenceThreshold: optionalNumber(value.confidenceThreshold, `${path}.confidenceThreshold`),
    pageRules,
    fallback: {
      route: requireString(fallback.route, `${path}.fallback.route`),
      onUnknown: onUnknown as FallbackBehaviour,
      byReason: Object.keys(byReason).length > 0 ? (byReason as RoutingProfileRules['fallback']['byReason']) : undefined
    },
    expectations
  };
}

const CARRIER_SIGNAL_SOURCES: CarrierSignalSource[] = [
  'barcodeValue',
  'barcodeSymbology',
  'text',
  'ocr'
];

function parseCarrierSignatureSet(value: unknown, path: string): CarrierSignatureSet {
  isObject(value, path);
  isArray(value.signals, `${path}.signals`);

  const signals: CarrierSignal[] = value.signals.map((entry, index) => {
    const child = `${path}.signals[${index}]`;
    isObject(entry, child);

    const source = requireString(entry.source, `${child}.source`);
    if (!CARRIER_SIGNAL_SOURCES.includes(source as CarrierSignalSource)) {
      throw new WireFormatError(
        `${child}.source`,
        `expected one of ${CARRIER_SIGNAL_SOURCES.join(', ')}`
      );
    }

    const weight = requireNumber(entry.weight, `${child}.weight`);
    if (weight <= 0 || weight > 1) {
      throw new WireFormatError(`${child}.weight`, 'expected a weight in (0, 1]');
    }

    return {
      source: source as CarrierSignalSource,
      // `barcodeSymbology` compares names exactly; every other source compiles as a regex.
      pattern:
        source === 'barcodeSymbology'
          ? requireString(entry.pattern, `${child}.pattern`)
          : requireRegExp(entry.pattern, `${child}.pattern`),
      weight,
      label: requireString(entry.label, `${child}.label`)
    };
  });

  return { carrier: requireString(value.carrier, `${path}.carrier`), signals };
}

/** Parses a rule bundle payload, or throws {@link WireFormatError}. */
export function parseBundlePayload(value: unknown): RuleBundlePayload {
  isObject(value, 'bundle');

  const schemaVersion = requireNumber(value.schemaVersion, 'bundle.schemaVersion');
  if (schemaVersion !== BUNDLE_SCHEMA_VERSION) {
    throw new WireFormatError(
      'bundle.schemaVersion',
      `expected ${BUNDLE_SCHEMA_VERSION}, got ${schemaVersion}`
    );
  }

  isArray(value.profiles, 'bundle.profiles');
  if (value.profiles.length === 0) {
    throw new WireFormatError('bundle.profiles', 'a bundle with no profiles routes nothing');
  }

  let carrierSignatures: CarrierSignatureSet[] | undefined;
  if (value.carrierSignatures !== undefined && value.carrierSignatures !== null) {
    isArray(value.carrierSignatures, 'bundle.carrierSignatures');
    carrierSignatures = value.carrierSignatures.map((entry, index) =>
      parseCarrierSignatureSet(entry, `bundle.carrierSignatures[${index}]`)
    );
  }

  return {
    schemaVersion,
    profiles: value.profiles.map((profile, index) =>
      parseProfile(profile, `bundle.profiles[${index}]`)
    ),
    carrierSignatures,
    generatedAt: optionalString(value.generatedAt, 'bundle.generatedAt')
  };
}

// -----------------------------------------------------------------------------------------
// Document features
// -----------------------------------------------------------------------------------------

function parseRectMm(value: unknown, path: string): RectMm {
  isObject(value, path);
  return {
    xMm: requireNumber(value.xMm, `${path}.xMm`),
    yMm: requireNumber(value.yMm, `${path}.yMm`),
    widthMm: requireNumber(value.widthMm, `${path}.widthMm`),
    heightMm: requireNumber(value.heightMm, `${path}.heightMm`)
  };
}

function parsePage(value: unknown, path: string): PageFeatures {
  isObject(value, path);

  const orientation = requireString(value.orientation, `${path}.orientation`);
  if (orientation !== 'portrait' && orientation !== 'landscape') {
    throw new WireFormatError(`${path}.orientation`, "expected 'portrait' or 'landscape'");
  }

  let inkBox: InkBox | null = null;
  if (value.inkBox !== undefined && value.inkBox !== null) {
    const rect = parseRectMm(value.inkBox, `${path}.inkBox`);
    const box = value.inkBox as Record<string, unknown>;
    inkBox = {
      ...rect,
      aspect: requireNumber(box.aspect, `${path}.inkBox.aspect`),
      coverage: requireNumber(box.coverage, `${path}.inkBox.coverage`)
    };
  }

  const rawBarcodes = value.barcodes ?? [];
  isArray(rawBarcodes, `${path}.barcodes`);
  const barcodes: DetectedBarcode[] = rawBarcodes.map((entry, index) => {
    const child = `${path}.barcodes[${index}]`;
    const rect = parseRectMm(entry, child);
    const barcode = entry as Record<string, unknown>;
    return {
      ...rect,
      symbology: requireString(barcode.symbology, `${child}.symbology`),
      value: typeof barcode.value === 'string' ? barcode.value : ''
    };
  });

  let textLines: TextLine[] | undefined;
  if (value.textLines !== undefined && value.textLines !== null) {
    isArray(value.textLines, `${path}.textLines`);
    textLines = value.textLines.map((entry, index) => {
      const child = `${path}.textLines[${index}]`;
      const rect = parseRectMm(entry, child);
      return { ...rect, text: String((entry as Record<string, unknown>).text ?? '') };
    });
  }

  let ocrRegions: OcrRegion[] | undefined;
  if (value.ocrRegions !== undefined && value.ocrRegions !== null) {
    isArray(value.ocrRegions, `${path}.ocrRegions`);
    ocrRegions = value.ocrRegions.map((entry, index) => {
      const child = `${path}.ocrRegions[${index}]`;
      isObject(entry, child);
      return {
        rect: parseRectMm(entry.rect, `${child}.rect`),
        key: requireString(entry.key, `${child}.key`),
        text: String(entry.text ?? '')
      };
    });
  }

  let templateMatches: TemplateMatch[] | undefined;
  if (value.templateMatches !== undefined && value.templateMatches !== null) {
    isArray(value.templateMatches, `${path}.templateMatches`);
    templateMatches = value.templateMatches.map((entry, index) => {
      const child = `${path}.templateMatches[${index}]`;
      const rect = parseRectMm(entry, child);
      const match = entry as Record<string, unknown>;
      return {
        ...rect,
        template: requireString(match.template, `${child}.template`),
        score: requireNumber(match.score, `${child}.score`)
      };
    });
  }

  return {
    pageNumber: requireNumber(value.pageNumber, `${path}.pageNumber`),
    pageCount: requireNumber(value.pageCount, `${path}.pageCount`),
    pageWidthMm: requireNumber(value.pageWidthMm, `${path}.pageWidthMm`),
    pageHeightMm: requireNumber(value.pageHeightMm, `${path}.pageHeightMm`),
    orientation,
    rotation: optionalNumber(value.rotation, `${path}.rotation`) ?? 0,
    text: value.text === undefined || value.text === null ? null : String(value.text),
    textLines,
    inkBox,
    barcodes,
    ocrRegions,
    templateMatches
  };
}

/** Parses the features an agent posted for a server-side decision. */
export function parseDocumentFeatures(value: unknown): DocumentFeatures {
  isObject(value, 'features');
  isArray(value.pages, 'features.pages');

  const pages = value.pages.map((page, index) => parsePage(page, `features.pages[${index}]`));
  const pageCount = optionalNumber(value.pageCount, 'features.pageCount') ?? pages.length;

  return {
    fileName: requireString(value.fileName, 'features.fileName'),
    sourceApp: optionalString(value.sourceApp, 'features.sourceApp'),
    pageCount,
    pages
  };
}
