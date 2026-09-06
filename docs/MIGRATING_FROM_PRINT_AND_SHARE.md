# Migrating from Print&Share

Printo replaces Print&Share on the workstation. This is what carries over, what changes, and
how to run both side by side while you find out whether it routes your documents correctly.

The migration is deliberately reversible. Printo installs alongside Print&Share, watches its own
folders, and prints to the same physical printers. Nothing about installing it stops Print&Share
working, so the decision to switch is one you make after seeing the evidence, not before.

---

## 1. What maps onto what

| Print&Share | Printo | Notes |
|---|---|---|
| Server-side routing profiles | **Rule bundle**, published from the fleet console | One versioned document for the whole fleet, validated before it reaches a machine |
| Picture / snippet match | `image` predicate | Normalised cross-correlation against a reference image carried in the bundle |
| Text match | `text` predicate | Plus `withinRect`, so a rule can say "the words that decide this live *here*" |
| OCR match | `ocr` predicate | Run only for the rectangles a rule asks for, never speculatively |
| — | `barcode` predicate | New. Symbology, value, count, and position |
| — | `geometry` predicate | New, and the one that does most of the work — see §2 |
| — | `carrier` predicate | New. Weighted, scored, and traced |
| Printer selection per rule | `then.route` → a role (`A4`, `THERMAL`) or a named printer alias | Roles rather than queue names, so one rule set works across benches with different printers |
| Scaling / rotation / crop | `then.transform` | `source`, `padMm`, `rotate`, `fit`, `media`, `zoomPercent`, pan, copies, duplex, tray |
| Per-workstation configuration | `agent.json`, the MSI's properties, or Group Policy | Four layers with visible provenance — see `docs/DEPLOYMENT.md` §2.5 |
| — | Fallback picker | New. When routing is uncertain, an operator is asked rather than guessed at |
| — | Fallback analytics and review queue | New. Every prompt is logged with its trace; one click derives a rule from it |

### The one structural difference worth understanding

Print&Share rules are written mostly against **content**: this text, that picture. Printo's rules
are written against **measurement** first, and content second.

That is not a stylistic preference. It comes out of the corpus: in 1266 sample pages, the
outgoing label is almost never a whole page — it is a region on an A4 carrier sheet, and it has
to be found, cropped and scaled onto 100×150 mm stock. Where the region *is* and what shape it
is turns out to identify it far more reliably than what it says, not least because a
courier-generated label often has no text layer at all.

The practical consequence when you port a rule: start from `geometry` (ink width, height,
aspect), add `carrier` or `text` to disambiguate, and reach for `ocr` only when the page
genuinely has no other evidence. A rule that keys on text alone will work on the samples you
tested it against and fail on the first label that arrives as a flat image.

---

## 2. Porting a routing profile

Print&Share, roughly:

> *If the page contains the DHL logo picture with score > 0.8, print to ZEBRA at 100×150,
> rotated 90°.*

Printo:

```jsonc
{
  "id": "dhl-outgoing-label",
  "name": "DHL parcel label",
  "when": {
    "all": [
      { "carrier": { "is": "DHL" } },
      {
        "geometry": {
          "inkWidthMm":  { "min": 90,  "max": 115 },
          "inkHeightMm": { "min": 140, "max": 200 },
          "inkAspect":   { "min": 1.4, "max": 2.1 }
        }
      }
    ]
  },
  "then": {
    "route": "THERMAL",
    "confidence": 0.95,
    "transform": { "source": "inkBox", "padMm": 1, "rotate": "auto", "fit": "contain" }
  }
}
```

Three things changed, each for a reason:

- **`route: "THERMAL"`, not a printer name.** The queue is resolved per machine from its printer
  map, so the same rule works on a bench with a CITIZEN and a bench with a ZEBRA.
- **`rotate: "auto"`, not 90°.** The engine compares the region's aspect with the media's and
  rotates when that is what makes it fit. Hard-coding 90° breaks the day a carrier emits the
  label the other way up.
- **`source: "inkBox"`.** The label is cropped out of the carrier sheet by measurement. Printing
  the whole A4 page scaled onto a 100×150 label is the single most common way this goes wrong.

**You do not have to port anything by hand to start.** Install the agent with the built-in
profile, let it run alongside Print&Share, and let the fallback queue tell you what it cannot
route. Each entry there derives its own rule from what it actually measured.

---

## 3. Running both at once

1. Deploy the server (`docs/DEPLOYMENT.md` §1) and the agent MSI to **one** bench first.
2. Point Printo at a **copy** of the folder Print&Share watches, not the same folder. Two
   watchers on one directory will both claim files.
3. Map the same physical printers in `agent.json`.
4. Leave Print&Share as it is. It keeps printing; Printo prints alongside it.
5. Compare output for a few days. The console's fallback analytics tell you where Printo was
   unsure and what the operator chose; the agreement rate tells you whether its proposals were
   already right.
6. When the fallback rate is acceptable, switch that bench's real folder over and roll out to
   the rest by GPO.

There is no import tool for Print&Share profiles, and there deliberately isn't one. The rule
models differ enough — measurement-first versus content-first — that a mechanical translation
would produce rules that look ported and route badly. Deriving them from your own documents,
through the fallback queue, produces rules fitted to what you actually print.

---

## 4. What Printo does that Print&Share did not

- **Every routing decision carries a trace.** Which rules were tried, which predicate failed,
  and the value it measured. "The fallback fired again" is replaced by
  *`dhl-label-embedded` failed at `geometry.inkAspect` because the measured aspect was 1.31 and
  the rule wanted at least 1.4*.
- **Nothing is guessed silently.** A page the engine cannot route with confidence goes to a
  keyboard-first picker rather than to whichever printer seemed likeliest.
- **The agent works with the server down.** In `auto` mode a confident local decision never
  touches the network, and an unreachable server still prints — recorded as having run on cached
  rules, so the drift is visible.
- **Carrier resolution is scored and explained**, not a keyword list. This is not academic: the
  legacy heuristic mis-attributed DHL labels to GLS because every MyDHL label carries the literal
  string `*GLS certified label*`.
- **Media, offsets, zoom and copies are configurable centrally and overridable per agent and per
  printer**, with the effective value and its source recorded on every job.

## 5. Picture matching

The closest thing to a direct Print&Share equivalent, and it works the same way in spirit: a
reference image, a threshold, and an optional search area.

```jsonc
// In the bundle, alongside `profiles`:
"templates": [
  { "name": "dhl-logo", "png": "<base64 PNG>", "dpi": 150, "description": "MyDHL header logo" }
]
```

```jsonc
// In a rule:
{ "image": { "template": "dhl-logo", "threshold": 0.8, "searchRect": { "unit": "pageFraction", "x": 0, "y": 0, "w": 1, "h": 0.3 } } }
```

Four things worth knowing before porting a snippet rule:

- **The score is normalised cross-correlation**, so it is invariant to brightness and contrast.
  The same logo printed lightly on one thermal head and heavily on another scores the same,
  which a plain pixel difference would not.
- **Scale is physical, not pixel.** The template's `dpi` says what resolution it was captured
  at, and the page is rendered to match, so a logo occupying 18 mm on the reference is looked
  for at 18 mm on the page. A template captured at the wrong dpi will not match.
- **It is lazy.** A page settled by an earlier geometry or text rule is never rasterized for a
  picture match at all. Put the cheap rules first.
- **Give it a `searchRect` when you can.** The whole page is searched otherwise, which works —
  a full A4 page at 150 dpi takes well under a tenth of a second — but narrowing it is both
  faster and less likely to match something else that happens to look similar.

Thresholds: 0.8 is a reasonable starting point. The trace records the score actually reached,
so a rule that is not firing tells you whether it missed by 0.01 or by 0.5.

## 6. What Printo does not do yet

Stated plainly, because the gap that matters most is the one closest to a Print&Share feature
you may rely on:

- **There is no tool for cutting a template out of a sample PDF.** Picture matching itself
  works — see §6 — but the reference image has to be produced by hand and pasted into the bundle
  as base64. Deriving a rule from a logged fallback is one click; cropping a logo is not.
- **The visual rule editor does not exist.** Rules are authored as JSON in the console, which
  validates them and names the exact path of anything either engine could not execute. Deriving
  a rule from a logged fallback is one click; drawing a rectangle on a sample PDF is not
  available.
- **Virtual-printer ingress is not built.** Printo currently takes work from watched folders.
  Capturing a `Ctrl+P` to a printer named `Printo` is blocked on the capture spike
  (`docs/WINDOWS_CLIENT_PLAN.md` §5.0). If your Print&Share workflow is "print to the virtual
  printer", that part is not ready; if it is "drop a file in a folder", it is.
- **No physical printer has printed from this system yet.** Composition and placement are
  verified against reference images and a real driver's printable geometry, but the hardware
  matrix is still to be run.
