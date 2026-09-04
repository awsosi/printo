import { describe, expect, it } from 'vitest';
import {
  computePlacement,
  DEFAULT_THERMAL_MEDIA,
  formatMedia,
  parseMedia,
  resolveMedia,
  resolveRotation,
  type MediaSize
} from '../src/index.js';

/** The measured DHL crop: 92x180mm portrait. */
const DHL_CROP = { xMm: 33.8, yMm: 15, widthMm: 92, heightMm: 180 };
/** The measured FedEx crop: a true 4x6in label. */
const FEDEX_CROP = { xMm: 34.3, yMm: 19.3, widthMm: 101.1, heightMm: 149.9 };

const MEDIA_100x150: MediaSize = { widthMm: 100, heightMm: 150 };
const MEDIA_100x200: MediaSize = { widthMm: 100, heightMm: 200 };

describe('parseMedia', () => {
  it('accepts free WxH mm values so any stock works without a code change', () => {
    expect(parseMedia('100x150mm')).toEqual({ widthMm: 100, heightMm: 150 });
    expect(parseMedia('100 x 150')).toEqual({ widthMm: 100, heightMm: 150 });
    expect(parseMedia('105x148 mm')).toEqual({ widthMm: 105, heightMm: 148 });
    expect(parseMedia('101.6x152.4mm')).toEqual({ widthMm: 101.6, heightMm: 152.4 });
    expect(parseMedia('100,5x150')).toEqual({ widthMm: 100.5, heightMm: 150 });
  });

  it('accepts named sizes', () => {
    expect(parseMedia('A4')).toEqual({ widthMm: 210, heightMm: 297 });
    expect(parseMedia('letter')).toEqual({ widthMm: 215.9, heightMm: 279.4 });
  });

  it('returns null rather than guessing', () => {
    expect(parseMedia('huge')).toBeNull();
    expect(parseMedia('0x0')).toBeNull();
    expect(parseMedia(undefined)).toBeNull();
  });

  it('round-trips through formatMedia', () => {
    expect(formatMedia({ widthMm: 100, heightMm: 150 })).toBe('100x150mm');
    expect(parseMedia(formatMedia(DEFAULT_THERMAL_MEDIA))).toEqual(DEFAULT_THERMAL_MEDIA);
  });
});

describe('resolveMedia precedence', () => {
  it('takes the most specific layer that is set, and reports which one', () => {
    const resolved = resolveMedia({
      agentPrinterMedia: '100x200mm',
      centralProfileMedia: '100x150mm',
      productDefault: DEFAULT_THERMAL_MEDIA
    });
    expect(resolved.value).toEqual({ widthMm: 100, heightMm: 200 });
    expect(resolved.layer).toBe('agent-printer');
  });

  it('lets a rule override every other layer', () => {
    const resolved = resolveMedia({
      ruleMedia: '105x148mm',
      agentPrinterMedia: '100x200mm',
      centralPrinterMedia: '100x150mm',
      productDefault: DEFAULT_THERMAL_MEDIA
    });
    expect(resolved.layer).toBe('rule');
    expect(resolved.value).toEqual({ widthMm: 105, heightMm: 148 });
  });

  it('falls back to the product default when nothing is configured', () => {
    const resolved = resolveMedia({ productDefault: DEFAULT_THERMAL_MEDIA });
    expect(resolved.layer).toBe('product-default');
    expect(resolved.value).toEqual({ widthMm: 100, heightMm: 150 });
  });

  it('ignores an unparseable value and continues down the chain', () => {
    const resolved = resolveMedia({
      ruleMedia: 'not-a-size',
      centralProfileMedia: '100x200mm',
      productDefault: DEFAULT_THERMAL_MEDIA
    });
    expect(resolved.layer).toBe('central-profile');
    expect(resolved.value).toEqual({ widthMm: 100, heightMm: 200 });
  });
});

describe('resolveRotation', () => {
  it('keeps a portrait crop upright on portrait stock', () => {
    expect(resolveRotation('auto', DHL_CROP, MEDIA_100x150)).toBe(0);
    expect(resolveRotation('auto', FEDEX_CROP, MEDIA_100x150)).toBe(0);
  });

  it('turns a portrait crop onto landscape stock', () => {
    expect(resolveRotation('auto', DHL_CROP, { widthMm: 200, heightMm: 100 })).toBe(90);
  });

  it('honours an explicit rotation', () => {
    expect(resolveRotation(180, DHL_CROP, MEDIA_100x150)).toBe(180);
    expect(resolveRotation(0, DHL_CROP, { widthMm: 200, heightMm: 100 })).toBe(0);
  });
});

describe('computePlacement', () => {
  it('scales the 92x180mm DHL crop down to fit 100x150 stock, centred', () => {
    const placement = computePlacement(
      { source: 'inkBox', rotate: 'auto', fit: 'contain' },
      DHL_CROP,
      MEDIA_100x150
    );

    // Height is the binding dimension: 150/180 = 0.8333.
    expect(placement.rotation).toBe(0);
    expect(placement.scaleX).toBeCloseTo(150 / 180, 6);
    expect(placement.destination.heightMm).toBeCloseTo(150, 6);
    expect(placement.destination.widthMm).toBeCloseTo(92 * (150 / 180), 6);
    // Centred horizontally, flush vertically.
    expect(placement.destination.xMm).toBeCloseTo((100 - 92 * (150 / 180)) / 2, 6);
    expect(placement.destination.yMm).toBeCloseTo(0, 6);
    expect(placement.reduced).toBe(true);
    expect(placement.clipped).toBe(false);
  });

  it('places the same crop near 1:1 on 100x200 stock', () => {
    const placement = computePlacement(
      { source: 'inkBox', rotate: 'auto', fit: 'contain' },
      DHL_CROP,
      MEDIA_100x200
    );

    // Width binds here: 100/92 = 1.087, but contain never exceeds the smaller ratio,
    // so the scale is min(100/92, 200/180) = 1.087 vs 1.111 -> 1.087.
    expect(placement.scaleX).toBeCloseTo(100 / 92, 6);
    expect(placement.destination.widthMm).toBeCloseTo(100, 6);
    expect(placement.destination.heightMm).toBeCloseTo(180 * (100 / 92), 6);
    expect(placement.clipped).toBe(false);
  });

  it('fits a 4x6in FedEx crop onto 100x150 stock with almost no reduction', () => {
    const placement = computePlacement(
      { source: 'inkBox', rotate: 'auto', fit: 'contain' },
      FEDEX_CROP,
      MEDIA_100x150
    );
    expect(placement.scaleX).toBeCloseTo(Math.min(100 / 101.1, 150 / 149.9), 6);
    expect(placement.scaleX).toBeGreaterThan(0.98);
    expect(placement.clipped).toBe(false);
  });

  it('cover fills the media and reports the overflow as clipped', () => {
    const placement = computePlacement(
      { fit: 'cover', rotate: 0 },
      DHL_CROP,
      MEDIA_100x150
    );
    expect(placement.scaleX).toBeCloseTo(Math.max(100 / 92, 150 / 180), 6);
    expect(placement.clipped).toBe(true);
  });

  it('actual keeps 1:1 regardless of media', () => {
    const placement = computePlacement({ fit: 'actual', rotate: 0 }, DHL_CROP, MEDIA_100x200);
    expect(placement.scaleX).toBe(1);
    expect(placement.destination.widthMm).toBeCloseTo(92, 6);
    expect(placement.destination.heightMm).toBeCloseTo(180, 6);
  });

  it('stretch fills both axes independently', () => {
    const placement = computePlacement({ fit: 'stretch', rotate: 0 }, DHL_CROP, MEDIA_100x150);
    expect(placement.scaleX).toBeCloseTo(100 / 92, 6);
    expect(placement.scaleY).toBeCloseTo(150 / 180, 6);
    expect(placement.destination.widthMm).toBeCloseTo(100, 6);
    expect(placement.destination.heightMm).toBeCloseTo(150, 6);
  });

  it('applies zoom and pan on top of the fit', () => {
    const placement = computePlacement(
      { fit: 'contain', rotate: 0, zoomPercent: 50, panXMm: 5, panYMm: -3 },
      DHL_CROP,
      MEDIA_100x150
    );
    expect(placement.scaleX).toBeCloseTo((150 / 180) * 0.5, 6);
    const width = 92 * (150 / 180) * 0.5;
    expect(placement.destination.xMm).toBeCloseTo((100 - width) / 2 + 5, 6);
  });

  it('rotates 90 degrees when the media is landscape, swapping the fitted dimensions', () => {
    const placement = computePlacement(
      { rotate: 'auto', fit: 'contain' },
      DHL_CROP,
      { widthMm: 200, heightMm: 100 }
    );
    // After rotation the crop is 180 wide x 92 high, so height binds: 100/92 = 1.087.
    const scale = Math.min(200 / 180, 100 / 92);
    expect(placement.rotation).toBe(90);
    expect(placement.scaleX).toBeCloseTo(scale, 6);
    expect(placement.destination.widthMm).toBeCloseTo(180 * scale, 6);
    expect(placement.destination.heightMm).toBeCloseTo(100, 6);
  });
});
