/**
 * Placement maths: `source region -> rotate -> fit -> place on media`.
 *
 * This is the part that decides whether a 92x180 mm DHL crop lands correctly on 100x150 mm
 * stock or on 100x200 mm, without per-site fiddling. It is pure arithmetic on purpose: the
 * render-diff tests assert margins, zoom and orientation here, with no printer involved.
 *
 * Media is a free `WxH mm` value rather than an enum, so any stock works without a code
 * change (plan section 7.4).
 */

import type { RectMm } from './features.js';
import type { FitSpec, RotateSpec, TransformSpec } from './rules.js';

export interface MediaSize {
  widthMm: number;
  heightMm: number;
}

/** Named sizes recognised in addition to free `WxH mm` values. */
const NAMED_MEDIA: Record<string, MediaSize> = {
  a4: { widthMm: 210, heightMm: 297 },
  a5: { widthMm: 148, heightMm: 210 },
  a6: { widthMm: 105, heightMm: 148 },
  letter: { widthMm: 215.9, heightMm: 279.4 },
  legal: { widthMm: 215.9, heightMm: 355.6 }
};

/** Product default thermal stock (plan section 11.2). */
export const DEFAULT_THERMAL_MEDIA: MediaSize = { widthMm: 100, heightMm: 150 };

/** Product default document stock. */
export const DEFAULT_DOCUMENT_MEDIA: MediaSize = NAMED_MEDIA.a4;

/**
 * Parses `100x150mm`, `100 x 150`, `100x150 mm` or a named size such as `A4`.
 * Returns `null` for anything unrecognised so the caller can fall back explicitly rather
 * than silently printing at the wrong size.
 */
export function parseMedia(value: string | undefined): MediaSize | null {
  if (!value) {
    return null;
  }

  const trimmed = value.trim().toLowerCase();
  const named = NAMED_MEDIA[trimmed];
  if (named) {
    return { ...named };
  }

  const match = /^(\d+(?:[.,]\d+)?)\s*[x×]\s*(\d+(?:[.,]\d+)?)\s*(mm)?$/.exec(trimmed);
  if (!match) {
    return null;
  }

  const width = Number(match[1].replace(',', '.'));
  const height = Number(match[2].replace(',', '.'));
  if (!Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) {
    return null;
  }
  return { widthMm: width, heightMm: height };
}

/** Renders a media size back to the canonical `WxHmm` form used in settings and logs. */
export function formatMedia(media: MediaSize): string {
  const round = (value: number): string =>
    Number.isInteger(value) ? String(value) : value.toFixed(1);
  return `${round(media.widthMm)}x${round(media.heightMm)}mm`;
}

export type Rotation = 0 | 90 | 180 | 270;

export interface Placement {
  /** Rotation applied to the source region before placing it. */
  rotation: Rotation;
  /** The region of the source page that is printed. */
  source: RectMm;
  /** Where it lands on the media, in mm from the top-left of the printable area. */
  destination: RectMm;
  /** Uniform scale, or the horizontal scale when `fit` is `stretch`. */
  scaleX: number;
  scaleY: number;
  /** True when the content was scaled down to fit; useful for a "reduced" warning. */
  reduced: boolean;
  /** True when part of the source falls outside the media (only possible with `cover`). */
  clipped: boolean;
}

function dimensionsAfterRotation(
  source: RectMm,
  rotation: Rotation
): { widthMm: number; heightMm: number } {
  return rotation === 90 || rotation === 270
    ? { widthMm: source.heightMm, heightMm: source.widthMm }
    : { widthMm: source.widthMm, heightMm: source.heightMm };
}

function scaleFor(
  fit: FitSpec,
  sourceWidth: number,
  sourceHeight: number,
  media: MediaSize
): { scaleX: number; scaleY: number } {
  const byWidth = media.widthMm / sourceWidth;
  const byHeight = media.heightMm / sourceHeight;

  switch (fit) {
    case 'actual':
      return { scaleX: 1, scaleY: 1 };
    case 'stretch':
      return { scaleX: byWidth, scaleY: byHeight };
    case 'cover': {
      const scale = Math.max(byWidth, byHeight);
      return { scaleX: scale, scaleY: scale };
    }
    case 'contain':
    default: {
      const scale = Math.min(byWidth, byHeight);
      return { scaleX: scale, scaleY: scale };
    }
  }
}

/**
 * `auto` rotation picks whichever of 0 or 90 degrees fits more of the source onto the media.
 * That is what makes a portrait 92x180 mm label land correctly on portrait 100x150 stock and
 * equally correctly on a landscape-fed printer, with no per-site configuration.
 */
export function resolveRotation(spec: RotateSpec | undefined, source: RectMm, media: MediaSize): Rotation {
  if (spec !== undefined && spec !== 'auto') {
    return spec;
  }

  const upright = scaleFor('contain', source.widthMm, source.heightMm, media);
  const turned = scaleFor('contain', source.heightMm, source.widthMm, media);
  return turned.scaleX > upright.scaleX ? 90 : 0;
}

/** Computes where a source region lands on the media. */
export function computePlacement(
  transform: TransformSpec | undefined,
  source: RectMm,
  media: MediaSize
): Placement {
  const fit: FitSpec = transform?.fit ?? 'contain';
  const rotation = resolveRotation(transform?.rotate, source, media);
  const rotated = dimensionsAfterRotation(source, rotation);

  const base = scaleFor(fit, rotated.widthMm, rotated.heightMm, media);
  const zoom = (transform?.zoomPercent ?? 100) / 100;
  const scaleX = base.scaleX * zoom;
  const scaleY = base.scaleY * zoom;

  const placedWidth = rotated.widthMm * scaleX;
  const placedHeight = rotated.heightMm * scaleY;

  const destination: RectMm = {
    xMm: (media.widthMm - placedWidth) / 2 + (transform?.panXMm ?? 0),
    yMm: (media.heightMm - placedHeight) / 2 + (transform?.panYMm ?? 0),
    widthMm: placedWidth,
    heightMm: placedHeight
  };

  const clipped =
    destination.xMm < -0.01 ||
    destination.yMm < -0.01 ||
    destination.xMm + destination.widthMm > media.widthMm + 0.01 ||
    destination.yMm + destination.heightMm > media.heightMm + 0.01;

  return {
    rotation,
    source,
    destination,
    scaleX,
    scaleY,
    reduced: scaleX < 1 || scaleY < 1,
    clipped
  };
}

/**
 * The layer a print setting came from.
 *
 * Every effective value is reported with its origin so "why did it print at that size" is
 * always answerable — in the tray, in the admin UI and on the job record (plan section 7.4).
 */
export type SettingLayer =
  | 'rule'
  | 'agent-printer'
  | 'agent-policy'
  | 'central-printer'
  | 'central-profile'
  | 'product-default';

export interface ResolvedSetting<T> {
  value: T;
  layer: SettingLayer;
}

/** Media candidates in precedence order, most specific first (plan section 7.4). */
export interface MediaResolutionInput {
  ruleMedia?: string;
  agentPrinterMedia?: string;
  agentPolicyMedia?: string;
  centralPrinterMedia?: string;
  centralProfileMedia?: string;
  productDefault: MediaSize;
}

/** Resolves the effective media through the fixed precedence chain. */
export function resolveMedia(input: MediaResolutionInput): ResolvedSetting<MediaSize> {
  const chain: Array<{ layer: SettingLayer; raw?: string }> = [
    { layer: 'rule', raw: input.ruleMedia },
    { layer: 'agent-printer', raw: input.agentPrinterMedia },
    { layer: 'agent-policy', raw: input.agentPolicyMedia },
    { layer: 'central-printer', raw: input.centralPrinterMedia },
    { layer: 'central-profile', raw: input.centralProfileMedia }
  ];

  for (const entry of chain) {
    const parsed = parseMedia(entry.raw);
    if (parsed) {
      return { value: parsed, layer: entry.layer };
    }
  }

  return { value: { ...input.productDefault }, layer: 'product-default' };
}
