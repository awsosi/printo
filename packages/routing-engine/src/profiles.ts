/**
 * The routing profile shipped with the product.
 *
 * Every numeric band below is a measurement from the 1266-page corpus, not a guess — the
 * figures come from `tools/corpus/extract_features.py` and are asserted by the golden-corpus
 * test. The comments quote the measured range so that widening a band is a deliberate,
 * reviewable act.
 *
 * Rule order encodes the laziness strategy (plan section 6.1): cheap text and geometry rules
 * first, OCR only for the pages that survive them. On the corpus this means 12 OCR calls in
 * normal mode instead of 736.
 *
 * **One rule set serves both input paths.** A document reaching the agent as a file keeps its
 * page frame; the same document printed through the virtual printer does not - the print path
 * turns landscape pages to portrait and replaces every non-A4 medium with the queue's own, so
 * `pageWidthMm`, `orientation` and `pageIsLabelStock` describe the printer rather than the
 * document (plan sections 5.0a and 5.0b). What survives is the ink box, and only as an
 * unordered pair of edges, because the artwork arrives turned.
 *
 * So the rules are written on `inkShortEdgeMm` and `inkAspectNormalised`, which read the same on
 * both paths, and the page-frame rules are kept only as *fast paths*: they fire first, cost
 * nothing, and settle the file path before anything is rasterized. A printed page falls straight
 * through them to the content rules below, which is where OCR earns its place. That ordering is
 * why unifying the two paths did not make the corpus more expensive to route - the 435 outgoing
 * FedEx labels are still claimed by geometry on the file path and never reach an OCR rule.
 */

import type { RoutingProfileRules } from './rules.js';

/**
 * Measured page geometry, in millimetres.
 *
 *   A4 landscape        297.0 x 210.0   FedEx label, DHL label, DHL waybill sheet
 *   A4 portrait         210.0 x 297.0   invoices, return notes, customs text
 *   DHL label stock      99.0 x 200.0   DHL label, full bleed
 *   UPS carrier sheet   231.1 x 318.2   UPS label embedded
 *   US Letter           215.9 x 279.4   FedEx return label
 */
const A4_LANDSCAPE = {
  orientation: 'landscape' as const,
  pageWidthMm: { min: 290, max: 305 },
  pageHeightMm: { min: 200, max: 220 }
};

/**
 * The default profile for the OneClickPrint document bundles.
 *
 * Named after the upstream system that emits them; a deployment can add profiles in front
 * of it, and the admin UI edits this one like any other.
 */
export const ONE_CLICK_PRINT_PROFILE: RoutingProfileRules = {
  profile: 'OneClickPrint',
  version: 1,
  match: { filenameMask: '*', minPages: 1 },
  confidenceThreshold: 0.75,
  pageRules: [
    {
      // The single most important rule in the product. The DHL courier sheet is
      // geometrically indistinguishable from the DHL parcel label — ink 92.2x183.6mm at
      // (34.0, 15.0) versus 91.9x180.3mm at (33.8, 15.0) — so only content can separate
      // them, and getting it wrong means the courier copy ends up on a parcel.
      id: 'dhl-waybill-sheet-text',
      name: 'DHL courier waybill sheet (text layer)',
      when: {
        any: [
          { text: { contains: 'Not to be attached to package' } },
          { text: { matches: '\\*\\s*WAYBILL\\s*DOC\\s*\\*' } },
          { text: { contains: 'Hand to Courier' } }
        ]
      },
      then: { route: 'A4', confidence: 1 }
    },
    {
      // FedEx return labels arrive on US Letter while outgoing FedEx labels arrive on A4
      // landscape, so the page size decides. Deliberately not keyed on the word "RETURN":
      // DHL outgoing labels carry `Ref No: Return` for return *shipments* and must still
      // print on thermal stock.
      id: 'fedex-return-label',
      name: 'FedEx return label on Letter',
      when: {
        all: [
          {
            geometry: {
              orientation: 'portrait',
              pageWidthMm: { min: 210, max: 220 },
              pageHeightMm: { min: 272, max: 288 },
              inkShortEdgeMm: { min: 88, max: 118 },
              inkAspectNormalised: { min: 1.35, max: 1.7 }
            }
          }
        ]
      },
      then: { route: 'A4', confidence: 0.9 }
    },
    {
      // DHL label printed on its own stock: 99x200mm page, ink 99.1x195.6mm at the origin,
      // coverage 22-25%. The whole page is the label, so no crop is needed - only a fit
      // onto whatever stock the site actually loaded.
      id: 'dhl-label-stock',
      name: 'DHL label on label stock',
      when: {
        all: [
          { geometry: { pageIsLabelStock: true, inkAspectNormalised: { min: 1.7, max: 2.2 } } }
        ]
      },
      then: {
        route: 'THERMAL',
        confidence: 1,
        transform: { source: 'page', rotate: 'auto', fit: 'contain' }
      }
    },
    {
      // FedEx label embedded in an A4 landscape sheet. Measured: ink 96.8-103.1mm wide,
      // aspect 1.48-1.50 (a 4x6in label), origin (34.3, 19.3). The aspect is what separates
      // it from the DHL family on the same page size, which sits at 1.96-2.00.
      id: 'fedex-label-embedded',
      name: 'FedEx outgoing label embedded in A4 landscape',
      when: {
        all: [
          {
            geometry: {
              ...A4_LANDSCAPE,
              inkAspectNormalised: { min: 1.35, max: 1.7 },
              inkShortEdgeMm: { min: 88, max: 118 }
            }
          }
        ]
      },
      then: {
        route: 'THERMAL',
        confidence: 0.95,
        transform: { source: 'inkBox', padMm: 1, rotate: 'auto', fit: 'contain' }
      }
    },
    {
      // UPS label embedded in a 231x318mm carrier sheet. Measured ink 99.1mm wide,
      // aspect 1.72-1.99, origin (10.4, 10.4).
      id: 'ups-label-embedded',
      name: 'UPS outgoing label embedded in carrier sheet',
      when: {
        all: [
          {
            geometry: {
              pageWidthMm: { min: 225, max: 240 },
              pageHeightMm: { min: 310, max: 326 },
              inkAspectNormalised: { min: 1.6, max: 2.1 },
              inkShortEdgeMm: { min: 88, max: 118 }
            }
          }
        ]
      },
      then: {
        route: 'THERMAL',
        confidence: 0.95,
        transform: { source: 'inkBox', padMm: 1, rotate: 'auto', fit: 'contain' }
      }
    },
    {
      // DHL label embedded in A4 landscape, identified from its text layer. Runs before the
      // OCR rule so that the 67 pages whose text layer names the product never get
      // rasterized. `dhl-waybill-sheet-text` has already removed the courier sheets, which
      // carry the same product names.
      id: 'dhl-label-embedded-text',
      name: 'DHL outgoing label embedded in A4 landscape (text layer)',
      when: {
        all: [
          {
            geometry: {
              ...A4_LANDSCAPE,
              inkAspectNormalised: { min: 1.75, max: 2.2 },
              inkShortEdgeMm: { min: 85, max: 118 }
            }
          },
          {
            any: [
              { text: { matches: 'EXPRESS\\s*WORLDWIDE' } },
              { text: { matches: 'ECONOMY\\s*SELECT' } },
              { text: { contains: 'MyDHL' } }
            ]
          }
        ]
      },
      then: {
        route: 'THERMAL',
        confidence: 0.95,
        transform: { source: 'inkBox', padMm: 1, rotate: 'auto', fit: 'contain' }
      }
    },
    {
      // The OCR gate, and the rule the whole product turns on: the DHL courier sheet is
      // geometrically indistinguishable from the DHL parcel label - 92.2x183.6mm against
      // 91.9x180.3mm - so only content separates them, and putting a courier copy on a
      // parcel is the worst thing this system can do.
      //
      // Deliberately not gated on the page frame. On the file path it is reached by the
      // template variant where the anonymiser flattened the static chrome into an image, so
      // `*WAYBILL DOC*` is visible but absent from the text layer. On the printed path it is
      // reached by every tall label-shaped region, because that is the only place the
      // distinction can still be made - the A4-landscape sheet the old rule keyed on does not
      // survive printing. Whitespace-insensitive matching is required: the recogniser returns
      // `*WAYBILLDOC*`.
      id: 'dhl-waybill-sheet-ocr',
      name: 'DHL courier waybill sheet (OCR)',
      when: {
        all: [
          {
            geometry: {
              inkShortEdgeMm: { min: 85, max: 118 },
              inkAspectNormalised: { min: 1.75, max: 2.2 }
            }
          },
          {
            ocr: {
              rect: 'inkBox',
              matches: 'WAYBILL\\s*DOC|Not\\s*to\\s*be\\s*attached|Hand\\s*to\\s*Courier'
            }
          }
        ]
      },
      then: { route: 'A4', confidence: 0.9 }
    },
    {
      // The return label, once printing has taken the page size away.
      //
      // On the file path the rule above catches these by their Letter page. Printed, a return
      // label and an outgoing label are the same rectangle - ink 100.8x151.6mm against
      // 101.1x149.9mm, both aspect 1.5 - so only content can separate them, and getting it
      // wrong sends a return label to a parcel printer.
      //
      // Keyed on `REF:` or `PO:` rather than the phrase `RETURN DEPT` that is printed on the
      // page. The recogniser reads a label in visual line order and this label's columns
      // interleave, so the two words come back separated by half the address block:
      // `PO: RETURN OJX058644430 REF: RETURN 808292H2ND03227236 DEPT:`. Either keyed form
      // appears on exactly 3 of the 1266 corpus pages, which are the three return labels;
      // bare `RETURN` appears on 467, because DHL outgoing labels carry `Ref No: Return` for
      // return *shipments* and must still print on thermal stock.
      id: 'fedex-return-label-ocr',
      name: 'FedEx return label (OCR)',
      when: {
        all: [
          {
            geometry: {
              inkShortEdgeMm: { min: 88, max: 118 },
              inkAspectNormalised: { min: 1.35, max: 1.7 }
            }
          },
          { ocr: { rect: 'inkBox', matches: 'REF:\\s*RETURN|PO:\\s*RETURN' } }
        ]
      },
      then: { route: 'A4', confidence: 0.9 }
    },
    {
      // Anything tall-label-shaped that survived the waybill checks is a parcel label.
      // Confidence is a notch below the text-identified rule because the evidence is geometric
      // plus the absence of waybill markings.
      //
      // The page frame is gone from this one too, which also makes it the rule that catches a
      // DHL label printed off its own stock and a UPS label printed off its carrier sheet -
      // both arrive as a tall 99x196mm region on plain A4, and both belong on thermal. It keeps
      // its name because the shape is the DHL family's and the corpus diagnostics quote the id.
      id: 'dhl-label-embedded',
      name: 'Tall label region (DHL, UPS and anything the same shape)',
      when: {
        all: [
          {
            geometry: {
              inkShortEdgeMm: { min: 85, max: 118 },
              inkAspectNormalised: { min: 1.75, max: 2.2 }
            }
          }
        ]
      },
      then: {
        route: 'THERMAL',
        confidence: 0.85,
        transform: { source: 'inkBox', padMm: 1, rotate: 'auto', fit: 'contain' }
      }
    },
    {
      // A 4x6in label region that is not a return. The counterpart to the rule above for the
      // FedEx family, and the rule that catches an outgoing FedEx label printed through the
      // virtual printer, where the A4-landscape sheet its fast path keys on no longer exists.
      //
      // Gated on the carrier as well as the shape, and that gate is doing real work: without it
      // this rule claims *any* 4x6in label region at a confidence above the threshold, which
      // silently routes an unknown carrier's label to thermal. That is exactly what the generic
      // rule below exists to prevent - a carrier the product has never seen must still work on
      // day one, but below the threshold, so the user confirms and the admin gets a review-queue
      // entry that can become a template (plan section 6.3, confirmed decision 7).
      //
      // The carrier is resolvable here because the rule above has already had the ink box
      // recognised, so `Fed Ex` and `TRK#` are available to the resolver as OCR signals on the
      // second pass. On the file path this rule is not reached at all: `fedex-label-embedded`
      // claims those pages by geometry long before.
      id: 'fedex-label-region',
      name: 'FedEx-shaped outgoing label region',
      when: {
        all: [
          {
            geometry: {
              inkShortEdgeMm: { min: 88, max: 118 },
              inkAspectNormalised: { min: 1.35, max: 1.7 }
            }
          },
          { carrier: { is: 'FEDEX' } }
        ]
      },
      then: {
        route: 'THERMAL',
        confidence: 0.85,
        transform: { source: 'inkBox', padMm: 1, rotate: 'auto', fit: 'contain' }
      }
    },
    {
      // Generic fallback for a carrier the product has never seen (plan section 6.3): a
      // label-shaped region carrying at least one shipping barcode is still cropped and sent
      // to thermal, but below the confidence threshold, so the user is asked to confirm and
      // the admin gets a review-queue entry that can become a template.
      id: 'generic-label-region',
      name: 'Unknown carrier, label-shaped region with a barcode',
      when: {
        all: [
          {
            geometry: {
              inkShortEdgeMm: { min: 70, max: 120 },
              inkAspectNormalised: { min: 1.3, max: 2.4 }
            }
          },
          { barcode: { minCount: 1 } }
        ]
      },
      then: {
        route: 'THERMAL',
        confidence: 0.6,
        transform: { source: 'inkBox', padMm: 1, rotate: 'auto', fit: 'contain' }
      }
    }
  ],
  fallback: {
    route: 'A4',
    // Everything the rules did not claim is a document, and documents are the majority of
    // every bundle. Prompting for those would make the picker useless, so the default route
    // is applied silently and only genuine ambiguity raises the picker.
    onUnknown: 'route',
    byReason: {
      LOW_CONFIDENCE: 'prompt',
      AMBIGUOUS: 'prompt',
      NO_THERMAL_CANDIDATE: 'prompt',
      UNKNOWN_CARRIER: 'prompt',
      CROP_IMPLAUSIBLE: 'prompt',
      OCR_UNAVAILABLE: 'prompt',
      RULE_HOLD: 'prompt',
      RENDER_FAILED: 'hold',
      DECODE_FAILED: 'hold',
      SERVER_UNAVAILABLE: 'prompt'
    }
  }
};

/** Profiles shipped with the product, in match order. */
export const BUILTIN_PROFILES: RoutingProfileRules[] = [ONE_CLICK_PRINT_PROFILE];

/** File-name stem a profile is exported under in `profiles/`. */
export function profileSlug(profile: RoutingProfileRules): string {
  return profile.profile
    .replace(/([a-z0-9])([A-Z])/g, '$1-$2')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '');
}
