# Windows Client + Routing Engine — Understanding & Delivery Plan

Status: `APPROVED — in delivery`
Last updated: 2026-09-06

**Progress:** M1 answered — Tier 1 (IPP) works (section 5.0), and what it revealed about the
print path (sections 5.0a and 5.0b) is now the open question. M2 complete. M3 complete except
the hardware pass, which the customer has postponed. M4 complete except virtual-printer
ingress, which is blocked on the rule-set decision in 5.0a, not on effort. See section 10.1.

This document is the proposal for the next phase of `printo`: a Windows agent that replaces
Print&Share on the workstation, plus the server-side work needed to make routing genuinely
more reliable than picture matching.

---

## 1. What the input data actually looks like

Analysed the full corpus in `C:\Users\olek\Documents\code\si\printo-materials`:
**258 PDFs / 1266 pages** across `wtorek_anon` (124), `czwart_anon` (82), `sroda_anon` (52).

### 1.1 Page-type census

> Superseded in part by **section 1.5**, which corrects this table from the full feature
> extraction: 12 of the 435 "FedEx" pages are a second DHL template variant, making the true
> split 423 FedEx / 145 DHL labels / 145 DHL courier sheets.

| Pages | Page type | Page size | Text layer | Route |
|---|---|---|---|---|
| 435 | FedEx label embedded in A4 landscape | 297x210 mm | none (image only) | **THERMAL** |
| 330 | Return Note | A4 portrait | native | A4 |
| 177 | Sales Invoice | A4 portrait | native | A4 |
| 139 | DHL `*WAYBILL DOC*` "Not to be attached to package - Hand to Courier" | A4 landscape | native | A4 |
| 72 | DHL label on real label stock | **99x200 mm** | native | **THERMAL** |
| 67 | DHL label embedded in A4 landscape | 297x210 mm | native | **THERMAL** |
| 20 | UPS label embedded in custom page | 231x318 mm | none (image only) | **THERMAL** |
| 18 | Invoice continuation / signature page | A4 portrait | sparse | A4 |
| 3 | FedEx **return** label on Letter | 216x279 mm | none | A4 (per current policy) |
| 5 | Customs declaration text (Y900/Y922) | A4 portrait | native | A4 |

Carriers present: **DHL Express**, **FedEx**, **UPS**. Document bundles are emitted by an
upstream system as `OneClickPrint_<ref>_anon.pdf`; the per-document page order varies
(`PPLPPLL`, `LL`, `PLPPL`, `PLLPPLLLL`, ... — 25+ distinct shapes across the corpus).
**Page position is not a usable routing key.**

### 1.2 The finding that drives the design

Only **72 of 159 outgoing labels sit on label-sized pages**. The rest are a 4x6-ish label
*region* placed inside a larger page. Measured ink bounding boxes:

```
case                          page mm         ink origin mm   ink size mm     h/w
DHL label on A4-landscape     297.0 x 210.0    33.8, 15.0      91.9 x 180.3   1.96
DHL label on label stock       99.0 x 200.0     0.0,  0.0      99.1 x 195.6   1.97
FedEx label on A4-landscape   297.0 x 210.0    34.3, 19.3     101.1 x 149.9   1.48  (= 4x6 in)
UPS label on 231x318 page     231.1 x 318.2    10.4, 10.4      99.1 x 196.8   1.99
FedEx return label on Letter  215.9 x 279.4    11.9, 12.4     100.8 x 151.6   1.50
Sales Invoice (A4 portrait)   210.0 x 297.0     9.9, 16.0     190.2 x 234.2   1.23
Return Note   (A4 portrait)   210.0 x 297.0     9.9, 15.0     190.2 x 253.7   1.33
```

So routing a *page* is not enough — the system must **crop the label region, rotate it,
and scale it onto the thermal media**. That is the single biggest functional gap versus
"send page N to the thermal printer".

### 1.3 Why picture matching alone fails here

```
DHL label       on A4-landscape:  ink 91.9 x 180.3 mm at (33.8, 15.0)
DHL WAYBILL DOC on A4-landscape:  ink 92.2 x 183.6 mm at (34.0, 15.0)
```

The DHL courier waybill sheet and the DHL parcel label are **geometrically near-identical** —
same page size, same position, same footprint, same logo band, same barcode block. Only the
content separates them (`*WAYBILL DOC*` / `Not to be attached to package`). A snippet/picture
matcher keyed on the DHL logo band will confuse the two; that is very likely a source of
today's misroutes. The new engine must combine **geometry + content (text / barcode / OCR)**,
never geometry alone.

### 1.4 Known corpus caveat

The FedEx and UPS labels in the anonymised set carry a text layer that the anonymiser
*added*; the production originals are image-only. Every classifier test therefore runs the
corpus twice: as-is, and in a **`--strip-text-layer` mode** that forces the barcode + OCR
path. A rule set that only passes with the synthetic text layer is considered failing.

### 1.5 Corrections from the M2 feature extraction

The census in 1.1 was produced by a text-keyword pass. Extracting the full feature set
(`tools/corpus/extract_features.py` → `tests/corpus/features.jsonl.gz`) corrected three
things. All three are load-bearing, so they are recorded rather than quietly fixed.

**a) The anonymiser destroyed the barcodes.** Only **4 of 1266 pages** carry a barcode that
`zxing-cpp` can decode, at any resolution up to 400 dpi (3 PDF417, 1 UPC-E). The rest were
replaced with same-sized noise. Consequences:

- The two barcode-keyed rules sketched in 6.2 (`^JD\d{18,20}$`, `minCount: 2`) **cannot be
  validated against this corpus**. Barcode predicates are implemented in both engines and
  covered by hand-written conformance fixtures, but their real-world behaviour is unproven.
- To close that gap we need either a handful of **non-anonymised** production PDFs, or an
  anonymiser change that re-encodes each barcode with fake-but-valid data of the same
  symbology. The second is the better fix and is a small change to
  `printo-materials/anonymize_invoices.py`. **Open with the customer.**

**b) 12 A4-landscape pages were mis-attributed to FedEx.** They are a second DHL template
variant — 6 parcel labels (ink 96.8x190.2 mm, coverage 6.5%) and 6 courier sheets
(96.8x192.5 mm, coverage 2.9%). The corrected totals are **423 FedEx labels, 145 DHL labels,
145 DHL courier sheets**.

**c) OCR is on the critical path, not an optimisation.** On that variant the anonymiser
flattened the static template chrome into an image: `*WAYBILL DOC*` and *"Not to be attached
to package"* are plainly visible on the page but **absent from the text layer**, which
contains only the anonymised field values. Geometry cannot separate those 12 pages
(the label and the sheet differ by 2.3 mm of ink height), so the only signal left is OCR.

This is why the shipped rule set ends with an OCR gate rather than treating OCR as a
last-resort nicety. Ink coverage *does* separate the two variants (2.9% vs 6.5%) and was
considered as a geometry-only discriminator; it was rejected because it is a property of how
much data a particular shipment happens to print, not of what the page *is* — exactly the
kind of correlation that holds on a sample and fails in production.

**Measured OCR cost.** With the rule order in `packages/routing-engine/src/profiles.ts`, the
corpus needs **12 OCR calls in normal mode** (the variant pages) and 218 with the text layer
stripped — against 736 pages that are label-shaped enough to be candidates. That ratio is the
whole point of the short-circuiting `all`/`any` described in 6.1.

---

## 2. Where the current system stands

`printo` today is a server-centric stack: `web` (admin) + `api` + `worker` + `vision` +
Postgres + Redis. The worker **scans SMB shares** and **dispatches to printers reachable from
the server** (`mock`/`socket`/`ipp`/`windows`/`cups`). Classification is `heuristic` (text
keywords) with an optional Python `vision` service.

That path stays and keeps working. What is missing is the other half of the product:
**the workstation is now both the intake and the output.** Jobs originate from a user pressing
Ctrl+P, and the destination printers are the ones installed on that PC — some USB, some
networked, invisible to the server.

Two concrete debts the corpus exposes in the existing engine:

- `apps/worker/src/classify/heuristic-classifier.ts` matches `/\bgls\b/i`, and every DHL label
  in the corpus contains the literal string `*GLS certified label*`. **Measured:** 278 pages
  carry the footer but only **4** were actually mis-attributed, because DHL is tested first
  and its patterns match the other 274 anyway. The 4 are DHL *DOMESTIC EXPRESS* labels, which
  matched none of the old DHL patterns — `\bdhl\b` does not match `MyDHL`. So the defect is
  real but was latent, kept in check by rule ordering rather than by anything principled;
  keyword-soup scoring is still not good enough. **Fixed** — see section 10.1.
- There is no crop/transform concept at all — a page is routed whole.

---

## 3. Decisions taken

| Decision | Choice |
|---|---|
| Client stack | **C# / .NET 10 LTS, win-x64**, self-contained |
| OS baseline | **Windows 10 22H2 and Windows 11**, installer runs as local admin |
| Decision split | **Both**, switchable per agent: `local` / `server` / `auto` |
| Thermal routing | **Outgoing carrier label only.** Waybill sheet and return label stay A4 |
| Configurability | Routing profiles must be editable like Print&Share: picture, text, OCR |
| Code signing | **Internal ADCS-issued Authenticode cert**; devices are domain-joined, trust + AV exclusions pushed by GPO |
| Virtual printer name | `Printo` |
| Thermal media | Default **100x150 mm**, fully configurable centrally *and* overridable per agent / per printer |
| Fallback | When routing is unavailable or unreliable, prompt the user with a keyboard-first page picker (section 7) |
| Fleet | 20-30 domain-joined workstations; MSI deployed by GPO, configured by ADMX |
| Server delivery | On-prem `docker compose up -d` / `down`, `.env` policy, internal HTTP, fronted by **Traefik using the file provider** (dynamic YAML, no docker labels) |
| UI language | **English only** |

---

## 4. Target architecture

```
        WORKSTATION                                  CENTRAL (existing docker stack)
 +------------------------------+               +----------------------------------+
 | Printo Agent Service         |               |  api    routing profiles,        |
 |  (LocalSystem, autostart)    |               |         label templates,         |
 |                              |   enroll      |         printer maps, accounting |
 |  +- capture ---------------+ |<------------->|                                  |
 |  | virtual printer ingress | |  bundle sync  |  worker  SMB intake (unchanged),  |
 |  | hot-folder watchers     | |  job events   |          server-side routing      |
 |  +------------------------+  |  escalation   |                                  |
 |  +- spool (SQLite) -------+  |<------------->|  vision  barcode + OCR + raster   |
 |  | dedupe, retry, outbox  |  |               |                                  |
 |  +------------------------+  |               |  web     admin UI, rule editor,   |
 |  +- routing engine -------+  |               |          review queue             |
 |  | same rules as server   |  |               +----------------------------------+
 |  +------------------------+  |
 |  +- print output ---------+  |  named pipe   +------------------------------+
 |  | PDFium -> printer DC   |  |<------------->| Printo Tray (per-user)       |
 |  | raw ZPL passthrough    |  |               | status, queue, reprint,      |
 |  +------------------------+  |               | settings, per-user printers  |
 +------------------------------+               +------------------------------+
```

**Why the service/tray split:** a Windows Service runs in session 0 and cannot see per-user
printer connections (`\\server\queue`) or show UI. The service owns capture, state, retry and
server communication; the tray agent owns the user session — it enumerates that user's
printers and executes prints under the user's identity. Machine-wide printers can be printed
by either. This is the same split Print&Share uses and it is the only robust arrangement.

### 4.1 Projects

```
clients/windows/
  Printo.Agent.Core/       capture, spool, rules engine, transforms, printing, API client
  Printo.Agent.Service/    Windows Service host  ("Printo Agent")
  Printo.Agent.Tray/       WinForms tray + minimal settings/status window
  Printo.Agent.Cli/        printo-agent.exe - diagnostics, test print, enroll, dump plan
  Printo.Agent.Setup/      WiX v5 MSI (service, tray autostart, virtual printer, firewall)
  Printo.Agent.Tests/      unit + corpus conformance + render-diff tests
```

---

## 5. Print job capture

No single capture mechanism is safe to bet on across Win10 22H2 -> Win11 25H2 with Windows
Protected Print Mode arriving. The agent therefore implements **tiers with runtime capability
detection**, and the installer picks the best available one.

| Tier | Mechanism | OS | Format received | Notes |
|---|---|---|---|---|
| **1** | **Local IPP endpoint.** Agent hosts IPP/1.1 on `127.0.0.1:<port>`; installer runs `Add-Printer -IppURL http://127.0.0.1:<port>/ipp/print` (inbox *Microsoft IPP Class Driver*) | Win10 1809+, Win11 | PDF if we advertise `document-format-preferred: application/pdf`, else PWG Raster | No third-party driver, no signing, **survives WPP**, gives full IPP job attributes (user, job name, copies, media) for accounting |
| **2** | **Inbox `Microsoft Print To PDF`** printer instance bound to a redirected port owned by the agent | Win10 22H2, Win11 | PDF | Inbox v4 driver, no signing. Job/user metadata correlated via `FindFirstPrinterChangeNotification` |
| **3** | **PSA v4 Virtual Printer** (MSIX, `windows.printSupportVirtualPrinterWorkflow`) | Win11 24H2+ only | OXPS -> PDF via inbox `PrintWorkflowPdlConverter`, or direct PDF passthrough | Microsoft's strategic path. MSIX signing is now possible with the ADCS cert (chain pushed to Trusted Publishers by GPO). Still Win11 24H2+ only, so it is an **opportunistic upgrade, never the only path** |
| **0** | **Hot folders** - always on, independent of the above | any | whatever is dropped | Configurable directories, extensions and filename masks |

**Spike first.** Tiers 1 and 2 each have one unverified question (does the IPP class driver
hand us `application/pdf`, and will the Print-to-PDF driver bind to a non-`PORTPROMPT` port).
Milestone M1 answers both on real hardware before any production code depends on the answer.
If Tier 1 returns PWG Raster we still ship it — the engine consumes rasters natively — but we
lose the text layer and lean harder on barcode + OCR.

### 5.0 M1 capture spike - **ANSWERED, Tier 1 confirmed**

Run on Windows 11 with `clients/windows/spike/scripts/Run-CaptureSpike.ps1`, printing
`czwart_anon/OneClickPrint_VTW189036998_anon.pdf` from Chrome to a queue created with
`Add-Printer -IppURL`. The captured job is checked in at
`tests/capture/chrome-ipp-a4-landscape-dhl.pdf`, and `CaptureRoutingTests` routes it.

**Tier 1 works, and it delivers PDF.**

| Question | Answer |
|---|---|
| Which tier | Tier 1. Inbox **Microsoft IPP Class Driver** bound to a local IPP endpoint. No driver to write, none to sign, no port monitor. Windows creates the port itself. |
| Which format | **`application/pdf`**, 223 710 bytes, IPP 2.0, user agent `wPrintWindowsDoc`. Not PWG Raster - the format question is settled in the good direction. |
| Operation flow | Nine `Get-Printer-Attributes`, then `Validate-Job`, `Create-Job`, `Send-Document` with `last-document=true`. All nine attribute requests must be answered or the queue does not bind. |
| Job identity | `job-name` carries the **original file name**, and `requesting-user-name` the submitting `DOMAIN\user`. Both are what the spool and accounting want, and both arrive free. |
| Job settings | `copies`, `media-col` (`media-size` in hundredths of a millimetre: `20999x29699` is A4), `media-type`, `sides`, `print-color-mode`, `print-quality`, `orientation-requested`, `printer-resolution`. |

**What the spooler does to the page, which matters more than the format.**

The renderer is `Microsoft: Print To PDF`. Measured against the source:

| | Source on disk | Delivered by the spooler |
|---|---|---|
| Sheet | 297x210 mm landscape | **210x297 mm portrait** |
| Ink box | 101.4 x 149.9 mm at (34, 19.3), aspect 1.48 | **150.6 x 101.6 mm at (19.1, 161.5), aspect 0.68** |
| Text layer | 180 characters | **0 characters** |
| Embedded image | 800x1228 at 200 dpi | 1228x800 at 199 dpi - **passed through, not re-rasterised** |

Three consequences, each of which changes production code:

1. **Image fidelity is preserved.** The page's images arrive at their native resolution rather
   than re-sampled, so barcode decoding and OCR are no worse than on the source file. This was
   the main risk of the IPP path and it did not materialise.

2. **There is no text layer.** The source's text was the anonymiser's invisible layer and the
   print path renders visible content only. Production originals are image-only anyway
   (section 1.5), and the engine is proven at 1266/1266 with `--strip-text-layer`, so this is
   survivable - but a rule set that quietly depends on text passes every existing test and
   fails on every real print job. `CaptureRoutingTests` pins it.

3. **The page arrives rotated, and the shipped rules do not recognise it.** Chrome asked for
   portrait stock; Windows turned the landscape sheet to fit. The label is still there and
   still the same size in millimetres, on its side. Every embedded-label rule is stated in a
   frame that turned with it - `orientation: landscape`, `pageWidthMm 290-305`,
   `inkAspect 1.35-1.7` - so `fedex-label-embedded` matches the file on disk and **nothing**
   matches the same document printed. It routes to A4 at confidence 0.60.

   Worse, it does so **silently**: the profile's `onUnknown` is `route`, which is right for the
   many pages that are ordinary documents, and its `expectations.thermalPagesPerDocument` is
   null, so nothing notices that a mixed document produced no label at all. A carrier label
   printing on A4 with no prompt is the exact failure this product exists to end.

This is why no production code was allowed to assume an answer. Virtual-printer ingress cannot
be built as "hot folders, but from the spooler": the routing has to be made rotation-tolerant
first, or the feature ships broken for the input it exists to handle.

### 5.0a What the print path does, measured across seven documents

One document is an anecdote. `Capture-Corpus.ps1` printed a curated spread from Chrome - one
document per page-shape family in the census, plus a page of nothing but visible prose - and
`compare_captures.py` and `CaptureRoutingTests` measured all 22 resulting pages. The captures
are checked in under `tests/capture/`.

**Four transformations, and each takes a class of predicate with it.**

| Transformation | Pages | What it costs |
|---|---|---|
| Text layer removed | **22 of 22** | Every `text` predicate. Including the HTML probe: 368 characters of ordinary prose, no images, comes back with nothing extractable. Glyphs become marks. |
| Landscape turned to portrait | **10 of 22** | `orientation`, and `pageWidthMm` / `pageHeightMm` for those pages. |
| Non-A4 media replaced by A4 | **5 of 22** | `pageIsLabelStock`, and every page-size predicate, for label stock (99x200) and the UPS carrier sheet (231x318). |
| Images resampled down to ~300 dpi | 4 of 22 | Nothing: 300 dpi is ample for barcodes and OCR, and nothing is ever resampled *up*. |

**What survives, and it is the important part: physical size.** Content is placed at 1:1 on the
new sheet rather than scaled to it. `SCALED` does not appear once in 22 pages. An ink box that
measured 101.6 x 156.0 mm on disk measures 150.6 x 101.6 mm printed - the same rectangle,
turned - and a 99 x 200 mm label page's ink lands at 99.3 x 195.8 mm on A4, in the corner.

**Why "make matching rotation-tolerant" is not enough.** Measured against the shipped rules,
transposing a page's geometry recovers the three embedded FedEx/DHL labels, whose rules key on
an A4-landscape sheet. It recovers none of the others, because no transposition brings back a
page size that was substituted away:

| Label page, as delivered | Rule that should match | Why it still fails |
|---|---|---|
| ink 150.6x101.6, aspect 0.68 | `fedex-label-embedded` | fixed by transposing: 101.6x150.6, aspect 1.48, on a 297x210 sheet |
| ink 99.3x195.8 at (0,0) | `dhl-label-stock` | needs `pageIsLabelStock`; the page is A4 now, and turning it does not make it 99x200 |
| ink 99.6x197.4 | `ups-label-embedded` | needs a 231x318 sheet; that size no longer exists on this path |

**And geometry alone cannot finish the job anyway.** Sorting the 22 measured ink boxes by their
short edge separates the classes - labels at 99-102 mm, courier waybill sheets at 92-94 mm,
invoices at 190+ mm - but only by a 5 mm margin, and section 1 already established from the
corpus that the DHL waybill sheet and the DHL parcel label sit at 92.2x183.6 and 91.9x180.3 mm.
Those two are not separable by any measurement. Only content separates them, and on this input
path content means OCR, barcodes or picture matching, because text is gone.

**The consequence for the rule set.** On the virtual-printer path:

- `text` predicates are dead. The OCR equivalents carry that load, and OCR stops being an
  optimisation for scanned pages and becomes load-bearing for every job.
- Page-frame predicates - `pageWidthMm`, `pageHeightMm`, `orientation`, `pageIsLabelStock` -
  describe the queue's media, not the document. They are evidence about the printer.
- The ink box is the reliable measurement, once normalised for orientation.
- Barcode and picture matching are the discriminators of last resort, and both are implemented.
  Barcode decoding remains unvalidated against real barcodes (section 1.5a) and this raises the
  cost of leaving it that way.

This does not invalidate the corpus work: 1266/1266 still holds for documents that reach the
agent as files, which is the hot-folder path and the majority of the existing rule set's
purpose. It means the printed path needs rules stated in terms that survive printing.

What that costs, and one measurement that changes how the ink box must be handed to the
recogniser, is section 5.0b.

### 5.0b What each stage of the pipeline costs, measured on the same captures

Section 5.0a established that the print path makes OCR load-bearing. The open question that
followed was whether that is affordable, and it was left as an assumption in both directions -
"OCR is expensive" and "OCR is fine" - so it is now measured. `CaptureCostTests` times every
stage on all 22 captured pages, median of three runs, and separates the cost every page pays
from the cost only an escalation pays.

Measured on the development workstation (Win11, **Debug** build, `pl` recogniser), on an idle
machine and repeated: two independent runs agree to within a few percent. A Release build and a
faster machine both move these down, and running them while the rest of the test suite competes
for the same cores roughly doubles every figure — the ratios between stages are the durable part.

| Stage | When it is paid | min | median | p90 | max |
|---|---|---:|---:|---:|---:|
| `geometry` — page box, text layer, ink box | every page | 4.0 | **10.7** | 18.1 | 18.5 |
| `barcodes` — zxing at 200 dpi, retried at 300 | every page | 162.6 | **216.1** | 254.7 | 263.9 |
| `render-200` — rasterise only, for comparison | — | 3.7 | 13.4 | 22.6 | 23.8 |
| `render-300` — rasterise only, for comparison | — | 5.1 | 20.4 | 27.5 | 31.8 |
| `ocr-ink` — recognise the ink box | on escalation | 39.4 | **86.5** | 183.6 | 188.7 |
| `ocr-page` — recognise the whole page | on escalation | 83.5 | 174.5 | 205.1 | 257.6 |
| `template-ink` — match one template in the ink box | on escalation | 94.7 | **133.0** | 186.8 | 242.4 |
| `template-page` — match one template over the page | on escalation | 183.6 | 199.1 | 216.7 | 253.2 |

Per page that is **231 ms always paid**, plus about **87 ms** for each page that escalates to OCR.
Cold, on the first job after a service start: geometry 40 ms, barcodes 470 ms, OCR 463 ms,
template 305 ms — roughly a second and a half of one-time initialisation, paid by whoever prints
first.

**Three things follow, and none of them is the one that was assumed.**

**1. OCR is not the expensive stage. Barcode decoding is, and it is the one running
unconditionally.** `AgentService` builds the extractor with a `ZxingBarcodeDecoder`, so every
page of every job is scanned for barcodes before a single rule is evaluated — 216 ms a page,
against 87 ms for the OCR that only escalating pages pay. Rasterisation is not what costs:
rendering the same page at 200 and 300 dpi is 34 ms of that 216, so about 180 ms is the scanning
itself, and a shared raster would buy back very little. The lever is that barcodes are eager
while OCR and picture matching are lazy. Making barcode decoding lazy — requested by the engine,
for the pages a rule actually asks about, exactly as OCR and templates already are — takes the
always-paid cost from 231 ms a page to about 11 ms, which is more than every OCR call in a
typical job costs.

Note also that this 525 ms is an upper bound for a reason worth keeping in view: **nothing
decoded on any of the 22 pages**, because the anonymiser destroyed the barcodes (section 1.5a).
The decoder therefore takes its failure path on every page — the 200 dpi pass finds nothing and
the 300 dpi retry runs too. Real barcodes would usually settle it on the first pass. The exact
saving cannot be measured until that gap is closed.

**2. OCR of the ink box is cheaper than OCR of the page, and reads at least as much.** 87 ms
against 174 ms, for the same or more text on every page. The ink box is already the measurement
5.0a identified as the one that survives printing, and it is also the right OCR region.

**3. The recogniser is orientation-sensitive, and the print path turns pages.** This surfaced as
an apparent impossibility in the numbers: on several pages OCR of the ink box returned *fewer*
characters than OCR of the whole page, and on one it returned none at all against 51 for the
page — although the ink box contains all the ink and both render at 250 dpi.

The cause is orientation, and `MeasuresWhatTurningTheInkBoxRecoversForTheRecogniser` measures it
by recognising each ink box at all four quarter turns. The recovery is not marginal:

| Page | ink box | 0° | 90° | 180° | 270° |
|---|---|---:|---:|---:|---:|
| vki189056401 p7 | 183.6x92.5 mm | 17 | **960** | 13 | 711 |
| vki189056401 p3 | 183.6x92.0 mm | 4 | **954** | 34 | 686 |
| vtw189048823 p2 | 183.9x92.0 mm | 30 | **905** | 25 | 609 |
| vtw189036998 p1 | 150.6x101.6 mm | 0 | **322** | 53 | 208 |
| vki189056401 p1 | 190.5x234.4 mm | **2192** | 148 | 1621 | 199 |
| vtw189053882 p2 | 99.6x197.4 mm | **1179** | 5 | 833 | 3 |

**Exactly 10 of the 22 pages read best turned — the same 10 the print path turned from landscape
to portrait.** A page that yields 4 characters upright and 954 turned is not a page OCR failed
on; it is a page nobody turned the right way up.

**The turn does not have to be searched for.** Every document in this corpus is portrait-native
content — labels are tall, invoices are tall — so ink measuring wider than it is tall is ink the
print path turned. That predicate picks the best of the four turns on **22 of 22 pages**, and the
test asserts it, because the difference is 100 ms a page against 692 ms for trying all four: the
search costs seven times as much and finds the same answer.

One honest limit: every turned page in this corpus was turned the same way, so 270° never wins
and the corpus cannot distinguish "turn it 90°" from "turn it back the way it came". An
implementation should treat 270° as the fallback when 90° yields nothing, which costs a second
recognition only on the page where the first one failed.

**What this settles for the decision in section 5.0a.** Content-based routing on the printed path
is affordable: the seven-page job in this corpus pays about 1.6 s of always-paid cost today, and
roughly 90 ms more for each page that escalates to OCR — against a print job the user is already
waiting seconds for. Cost is not a reason to prefer one rule-set shape over the other. The
efficiency work with the largest return is not avoiding OCR but making barcode decoding lazy,
and that is worth doing whichever shape is chosen. And any rule set for this path must normalise
the ink box for orientation *before* handing it to the recogniser, not only before comparing
geometry.

### 5.1 Hot-folder mode (robustness rules)

- Watch N configurable directories; per-directory extension list + include/exclude filename
  masks (glob and regex), recursion toggle.
- **Stability gate** before pickup: size and mtime unchanged across two polls *and* an
  exclusive-open probe succeeds — never read a file still being written.
- **Dedupe** on `sha256(content)` primary, `(path, size, mtime)` secondary; both persisted in
  SQLite with a configurable retention window, so a re-dropped identical file is ignored and a
  genuinely re-issued document is not.
- Post-action per directory: leave / move to `archive/` / move to `failed/` / delete.
- Crash-safe: a file is claimed in SQLite before processing; an interrupted claim is reclaimed
  on restart. Bounded retries with exponential backoff, then a poison queue that surfaces in
  the tray and on the server.

---

## 6. The routing engine

One declarative rule format, **two implementations** (C# in the agent, TypeScript in the
worker), kept honest by a shared golden-corpus conformance suite that runs in CI against both.

### 6.1 Evaluation pipeline (per page)

```
1. geometry     page size, orientation, rotation, ink bounding box (position, size, aspect)
2. text layer   pdfium text + positions, when present
3. barcodes     symbology + value + rect  (Code128/GS1-128, PDF417, MaxiCode, DataMatrix, QR)
4. OCR          only when 2 and 3 are insufficient, and only inside the rects a rule asks for
5. picture      snippet/template match with score  (Print&Share parity)
6. rules        first matching page rule wins -> route + transform
7. fallback     profile default, or "hold for review"
```

Steps 3-5 are lazy: a page that a text rule already resolves at high confidence never gets
rasterized. This is what makes it fast enough to run on the workstation.

### 6.2 Rule schema (sketch)

```jsonc
{
  "profile": "Marendo OneClickPrint",
  "match": { "filenameMask": "OneClickPrint_*.pdf", "sourceApp": "*", "minPages": 1 },
  "pageRules": [
    {
      "name": "DHL courier waybill sheet - never thermal",
      "when": { "all": [
        { "text": { "contains": "Not to be attached to package" } }
      ]},
      "then": { "route": "A4" }
    },
    {
      "name": "DHL outgoing label",
      "when": { "all": [
        { "carrier": { "is": "DHL" } },
        { "barcode": { "symbology": ["CODE_128"], "valueMatches": "^JD\\d{18,20}$", "minCount": 1 } },
        { "not": { "text": { "matches": "Ref No:\\s*Return" } } },
        { "geometry": { "inkAspect": { "min": 1.6, "max": 2.3 },
                        "inkWidthMm": { "min": 80, "max": 115 } } }
      ]},
      "then": {
        "route": "THERMAL",
        "transform": { "source": "inkBox", "padMm": 2, "rotate": "auto",
                       "fit": "contain", "media": "100x200mm", "copies": 1 }
      }
    },
    {
      "name": "FedEx / UPS outgoing label",
      "when": { "all": [
        { "carrier": { "in": ["FEDEX", "UPS"] } },
        { "barcode": { "symbology": ["PDF_417", "MAXICODE", "CODE_128"], "minCount": 2 } },
        { "not": { "ocr": { "rect": "inkBox", "matches": "REF:\\s*RETURN" } } }
      ]},
      "then": { "route": "THERMAL",
                "transform": { "source": "inkBox", "rotate": "auto", "fit": "contain",
                               "media": "100x150mm" } }
    },
    {
      "name": "Legacy picture rule (Print&Share parity)",
      "when": { "image": { "template": "dhl-logo-band", "threshold": 0.86,
                           "searchRect": { "unit": "pageFraction",
                                           "x": 0, "y": 0, "w": 1, "h": 0.25 } } },
      "then": { "route": "THERMAL" }
    }
  ],
  "fallback": { "route": "A4", "onUnknown": "route" }
}
```

Predicates available: `text` (contains / matches / withinRect), `ocr` (same, forces OCR in a
rect), `barcode` (symbology, value regex, count, rect), `image` (snippet template + threshold +
search area), `geometry` (page size class, orientation, ink box size / aspect / position),
`carrier`, `pageIndex` (first / last / nth / range), plus `all` / `any` / `not`.

Actions: `route` (role `A4`/`THERMAL` or a named printer alias), `transform`, `copies`,
`stop`/`continue`, `hold`.

**Carrier resolution** is its own scored step (barcode symbology mix + value patterns + logo
template + text signatures) so `*GLS certified label*` inside a DHL label can no longer flip
the carrier — barcode evidence outranks a bare keyword.

### 6.3 Label templates

A `LabelTemplate` library ships with the product (DHL Express, FedEx, UPS to start; DPD, GLS,
InPost, Poczta Polska, TNT as stubs) and is extensible from the admin UI. Each template
declares detection predicates, how to derive the crop region (fixed rect / ink box /
barcode-cluster hull), target media and rotation.

**Generic fallback:** an unknown carrier whose ink box is label-shaped (aspect 1.3-2.4, width
70-120 mm) and which carries at least one shipping barcode is still cropped and routed to
thermal at reduced confidence — so a new carrier works on day one, and gets a template later
for full confidence.

### 6.4 Decision modes (per agent, configurable)

| Mode | Behaviour |
|---|---|
| `local` | Agent decides everything from the cached rule bundle. Works fully offline. Reports outcomes for accounting. |
| `server` | Agent uploads the document, server returns a per-page plan. For weak workstations. Falls back to `local` if the server is unreachable (configurable: fall back / hold / fail). |
| `auto` *(default)* | Agent decides locally; any page below the confidence threshold is escalated to the server's vision service; server verdict wins. |

### 6.5 Rule trace — the thing that makes improvement possible

Every page evaluation emits a **trace**, not just a verdict: which rules were tested, which
predicate in each rule failed and with what measured value, the carrier scores and their
evidence, the extracted geometry, the barcodes found (symbology + value + rect), and whether
OCR ran. The trace is kept with the job and uploaded to the server.

This is deliberate: without it, "the fallback fired again" is unactionable. With it, an admin
opens the review queue and sees, for example, `dhl-outgoing-label` failed at
`barcode.valueMatches ^JD\d{18,20}$` because the decoded value was `JD014600009...` with a
leading space — and fixes the rule in one edit.

### 6.6 Fallback: when routing is unavailable or unreliable

The engine never guesses silently. It raises a fallback with an explicit reason code:

| Code | Meaning |
|---|---|
| `NO_THERMAL_CANDIDATE` | Profile expects an outgoing label, no page qualified |
| `LOW_CONFIDENCE` | Best candidate below the profile threshold |
| `AMBIGUOUS` | Conflicting rules, or more label candidates than the profile expects |
| `UNKNOWN_CARRIER` | Label-shaped region found, carrier unresolved |
| `NO_PROFILE_MATCH` | No routing profile matched the document at all |
| `OCR_UNAVAILABLE` | A rule needed OCR and this machine has no recogniser (added during M4 — see below) |
| `SERVER_UNAVAILABLE` | `server` mode, server unreachable, policy is `prompt` |
| `RULE_HOLD` | A rule explicitly asked for confirmation |
| `CROP_IMPLAUSIBLE` | Crop region degenerate, off-page, or wildly off the expected aspect |
| `RENDER_FAILED` / `DECODE_FAILED` | PDF or barcode decode error |

Behaviour per reason is configurable per profile: `prompt` (default), `route` (fall through to
the A4 default), or `hold` (queue in the tray, no UI). Nothing is ever silently dropped.

`OCR_UNAVAILABLE` was added to this list during M4. It is deliberately distinct from
`LOW_CONFIDENCE`: it is a *machine* problem with a specific fix — install the OCR language
pack, or switch that agent to `server`/`auto` — rather than a rule that needs improving, and
the fallback analytics have to be able to tell those apart. The alternative considered was
failing the job, which is wrong: a missing language pack would burn the retry budget and
poison a perfectly printable document rather than asking the user a question they can answer
in one keypress.

### 6.7 The page picker

The users' workflow is Ctrl+P then Enter in Chrome, at speed. The picker must not break that
rhythm, so it is built as a **keyboard-first, zero-chrome, zero-prose window**:

```
+---------------------------------------------------------------+
|  [1] +--------+   [2] +--------+   [3] +--------+              |
|      |        |       |        |*      |        |              |
|      | thumb  |       | thumb  |       | thumb  |              |
|      | ~260px |       | ~260px |       | ~260px |              |
|      |        |       |        |       |        |              |
|      +--------+       +========+       +--------+              |
|                        selected                                |
|                                                                |
|  [4] +--------+   [5] +--------+                               |
|      ...                                                       |
|                                                                |
|  Enter = print      Esc = all A4                     (one line) |
+---------------------------------------------------------------+
```

- Appears immediately, centred on the **active** monitor, focused, always-on-top, no taskbar
  entry. Thumbnails are large (~260 px wide) and rendered from the real page so a label is
  recognisable at a glance.
- Keys: `1`-`9` toggle that page, arrows move, `Space` toggles, `Enter` prints, `Esc` sends
  everything to A4 (the safe, current-behaviour default). Mouse click also toggles.
- Any page the engine *did* consider a likely label is **pre-selected and marked**, so in the
  common near-miss case the user just presses Enter.
- No explanatory text beyond the single hint line — the users are trained in person.
- Runs in the per-user tray process (session 0 cannot show UI).
- Timeout: **none by default** — the job waits in the tray queue rather than printing
  something wrong. Configurable to auto-resolve to A4 after N seconds.
- Every picker interaction is logged with the reason code, the full rule trace, the page
  thumbnails and **what the user actually chose**. That last part is the training signal:
  the server's review queue turns "user picked page 3, engine picked nothing" into a proposed
  rule or template with one click.

---

## 7. Print output

### 7.1 Transform model

Every printed page is `source region -> rotate -> fit -> place on media`:

```
source:  page | inkBox | fixed rect (mm) | barcodeCluster      (+ padMm)
rotate:  auto | 0 | 90 | 180 | 270        (auto = match media orientation)
fit:     contain (default) | cover | actual | stretch
media:   named size, or explicit mm       zoom %, pan x/y mm, per-printer offset
copies, duplex, tray, colour/mono
```

`auto` rotation and `contain` are what make a 92x180 mm DHL crop land correctly on 100x150 mm
*or* 100x200 mm stock without per-site fiddling.

### 7.2 Rendering

- **PDFium** (`FPDF_RenderPageBitmap` into a DIB, then `StretchDIBits` onto the printer DC).
  Rendering to a DIB rather than straight to the HDC keeps preview and print pixel-identical
  and lets the tests assert output geometry without a printer.
- Printable-area correction from `GetDeviceCaps(PHYSICALOFFSETX/Y, PHYSICALWIDTH/HEIGHT)` —
  required to get edge-to-edge output on label stock.
- **Raw passthrough** (`StartDocPrinter` with datatype `RAW` + `WritePrinter`) for ZPL/EPL/PCL,
  used when the source is already printer language or when a printer profile selects
  `zplRaster` mode (PDFium raster -> `^GFA` for Zebra-compatible units).
- Thermal support strategy: **default is raster through the printer's own Windows driver** —
  works uniformly for CITIZEN, 4BARCODE and ZEBRA, USB or Ethernet. ZPL raw is opt-in per
  printer for sites that want it.

### 7.3 Printer profiles

Per printer, stored server-side and cached on the agent: role (`A4` / `THERMAL` / alias),
default media, DPI, printable-area calibration, darkness/speed (thermal), rotation and zoom/pan
overrides, ZPL mode, retry policy. The tray offers **calibration and test print** so a new site
can be set up without guessing.

### 7.4 Settings precedence

Nothing is hard-coded. Every print setting — media size above all — resolves through a fixed,
inspectable chain, most specific wins:

```
1. rule-level transform override        (this rule, this page)
2. agent printer profile override       (this machine, this queue)   <- local override
3. agent policy override                (this machine)               <- local override / ADMX
4. central printer profile              (server, this printer)
5. central routing profile default      (server, this profile)
6. product default                      (100x150 mm thermal, A4 document)
```

The effective value and the layer it came from are shown in the tray and in the admin UI, and
recorded on every job — so "why did it print at that size" is always answerable. Media is a
free `WxH mm` value, not an enum, so 100x150, 100x200, 105x148 or anything else works without
a code change.

---

## 7A. Deployment and configuration

### Server

- `docker compose up -d` / `docker compose down`, all knobs in `.env`, nothing else required.
- Services speak **plain HTTP internally**; **Traefik** terminates and routes in front of them,
  configured through the **file provider** — a static `infra/traefik/traefik.yml` plus watched
  dynamic YAML in `infra/traefik/dynamic/`. **No docker labels**, so routing can be changed
  without touching compose or restarting containers.
- The current compose is dev-shaped (`node:18` + `npm install` at boot + bind mounts). It gains
  proper multi-stage Dockerfiles and a production compose file with pinned images, healthchecks,
  restart policies, named volumes and log rotation. The dev compose stays for local work.
- Configurable retention: document blobs, page thumbnails, traces, job history and audit rows
  each get their own retention window, enforced by a scheduled cleanup task.

### Client

- **MSI deployed by GPO** (per-machine), signed with the internal ADCS Authenticode cert; the
  issuing chain is pushed to Trusted Root / Trusted Publishers by GPO, so install is
  warning-free and MSIX (Tier 3) is unlocked where the OS supports it.
- **ADMX/ADML templates** ship with the product: server URL, enrollment token, decision mode,
  confidence threshold, fallback policy, hot folders, printer role mapping, media overrides,
  log level. GPO-set values are read-only in the tray and clearly marked as managed.
- Documented **AV/EDR exclusions** (service binary, spool directory, named pipe) for the GPO
  that manages endpoint protection.
- Unattended enrollment: the MSI takes the server URL and a one-time enrollment token, so a
  workstation is provisioned with no interactive step.

---

## 8. Server-side work

### 8.1 Data model (new migrations)

```
agents                    id, machine, os, version, user, decision_mode, status, last_seen
agent_enrollment_tokens   token, expires_at, used_by
agent_printers            agent_id, queue_name, driver, port, media[], dpi, role, alias
label_templates           id, carrier, variant, detect(jsonb), region(jsonb), media, version
routing_rule_sets         id, profile_id, version, rules(jsonb), published_at
rule_bundles              version, payload(jsonb), checksum        -- what agents sync
agent_jobs                agent_id, job_key, source(printer|folder), doc_sha256, pages, status
agent_job_pages           page_no, class, carrier, confidence, rule_id, route, printer,
                          transform(jsonb)
agent_job_events          ts, level, code, detail(jsonb)           -- accounting + audit trail
agent_job_page_traces     page_id, rule_id, outcome, failed_predicate, measured(jsonb)
fallback_events           job_id, reason_code, engine_selection[], user_selection[],
                          resolved_at, trace_ref, thumbnails_ref
review_queue              agent_job_page_id, reason, resolved_by, resolution
retention_policies        scope, window, last_run_at
```

`processed_files`, `print_jobs`, `print_job_pages` and the SMB path stay as they are.

### 8.2 API additions (`apps/api`)

```
POST /agents/enroll                     enrollment token -> agent id + credentials
GET  /agents/me/bundle?since=<version>  signed rule bundle (profiles, templates, printers)
POST /agents/me/printers                report discovered local queues + capabilities
POST /agents/me/heartbeat               health, version, queue depth
GET  /agents/me/commands                long-poll: reprint, pause, resync, collect diagnostics
POST /jobs                              create job (metadata + doc hash [+ PDF in server mode])
POST /jobs/{id}/plan                    server-mode: full per-page print plan
POST /jobs/{id}/pages/{n}/classify      auto-mode escalation for one page
POST /jobs/{id}/events                  progress / results / failures  (accounting)
POST /jobs/{id}/artifacts               page thumbnails for audit + review queue
```

Admin: CRUD for rule sets, templates, agents, agent printers; bundle publish/rollback; review
queue resolution.

### 8.3 Worker / vision

- Replace keyword-soup scoring with the shared rule engine; fix the GLS false positive.
- `vision` service gains: ink-bbox extraction, barcode decode with rects (zxing-cpp), region
  OCR, template match, and a `/classify-page` endpoint matching the agent's contract.
- Server-side routing gains the same crop/transform model, so SMB-sourced jobs benefit too.

### 8.4 Admin UI (`apps/web`)

- **Rule editor** — visual, Print&Share-shaped: upload a sample PDF, see rendered pages, drag a
  rectangle to define a text/OCR/picture region or a crop region, pick predicates, see the rule
  evaluate live against the sample and against the whole corpus.
- **Agents** — enrolled machines, versions, health, decision mode, printer mapping, remote
  commands.
- **Review queue** — pages the engine held or was unsure about, with one-click "route as X and
  learn this" that proposes a rule.
- **Fallback analytics** — every picker event with its reason code, the rule trace, the page
  thumbnails, what the engine proposed and what the user actually chose; grouped by reason and
  by document shape so the most common failure is obvious. This is the primary tool for driving
  the fallback rate towards zero.
- **Accounting** — jobs/pages per user, per agent, per printer, per carrier; export.

---

## 9. Test strategy

Nothing ships on "it looked right".

1. **Golden corpus.** All 258 PDFs get a per-page expected decision, bootstrapped by a
   labelling tool and reviewed once by hand. CI asserts the full corpus, in both normal and
   `--strip-text-layer` mode, against **both** engine implementations.
2. **Conformance suite.** A shared JSON fixture set (rule + page features -> expected decision)
   run by the C# and TypeScript engines. Divergence fails the build.
3. **Render-diff tests.** Every transform (crop/rotate/fit/media) renders to PNG and is compared
   to a checked-in reference — this is what proves margins, zoom and pan are right without a
   physical printer.
4. **Virtual printer harness.** A test `PrinterDevice` that writes what would have gone to the
   DC, so the print path is asserted end to end in CI.
5. **Hardware matrix.** Manual, documented, one pass per release: HP M60x / P3015 on A4;
   CITIZEN, 4BARCODE, ZEBRA on 100x150 and 100x200 stock; USB and Ethernet.
6. **Agent-server integration** against the docker compose stack, including server down, server
   slow, bundle rollback, and duplicate re-drop.
7. **Soak test.** The full corpus fed through hot folders at volume, asserting zero duplicates
   and zero losses across service restarts.

---

## 10. Delivery milestones

| # | Milestone | Content | Exit criteria |
|---|---|---|---|
| **M1** | Capture spike | Prove Tier 1 and Tier 2 on Win11 25H2 and Win10 22H2. Throwaway code. | Documented answer: which tier, which format. No production code depends on an unproven assumption. |
| **M2** | Corpus + engine core | Rule schema, C# + TS engines, feature extraction (geometry/text/barcode/OCR/picture), golden corpus, conformance suite. | 100% of corpus routed correctly in both engines, both text-layer modes. GLS false positive gone. |
| **M3** | Print output | PDFium render, transforms, printer profiles, calibration, raw/ZPL, render-diff tests. | Reference PNGs match; manual print on real A4 + thermal hardware verified. |
| **M4** | Agent runtime | Service + tray + IPC, SQLite spool, dedupe, retry, hot folders, virtual printer ingress, offline queue, **fallback page picker**. | Soak test passes; a hard kill loses nothing and duplicates nothing. Picker measured: Ctrl+P to on-screen under 1 s, Enter completes the job. |
| **M5** | Server integration | Migrations, agent APIs, bundle sync, decision modes, accounting, rule traces, fallback events, review queue, retention jobs. | All three decision modes verified, including server-unreachable behaviour. Every fallback carries a complete trace. |
| **M6** | Admin UI | Rule editor, agents, review queue, fallback analytics, accounting views. | A new carrier template can be created end to end from a sample PDF, no code changes. A logged fallback converts to a rule in one click. |
| **M7** | Packaging + delivery | WiX MSI signed by ADCS cert, GPO deployment, ADMX templates, AV exclusion doc, unattended enrollment. Server: production Dockerfiles, compose, Traefik file-provider config, `.env` policy. | Clean GPO install + upgrade + uninstall on Win10 22H2 and Win11, no residue. `docker compose up -d` brings the stack up behind Traefik from a clean checkout. |
| **M8** | Hardening | Full test matrix, docs, migration guide from Print&Share, CI green. | Definition of done in section 12 met. |

### 10.1 Status

| # | State | Evidence |
|---|---|---|
| **M1** | **Blocked** | Both spikes build and self-test; binding a real queue needs one elevated `Add-Printer`. Neither capture question is answered yet — section 5.0. |
| **M2** | **Complete** | 1266/1266 corpus pages routed correctly in **both** text-layer modes; 67 conformance fixtures pass on the TypeScript **and** C# engines; 0 pages attributed to GLS. The agent extracts its own features (geometry, ink box, text, barcodes, OCR) and `FeatureParityTests` proves they match the calibrated extractor — identical routing over 117 real pages, exact geometry, identical barcode decoding. Caveat unchanged: barcode predicates cannot be validated against real barcodes on this corpus (section 1.5a), and picture matching is now implemented (section 10.5), which adds four more fixtures. |
| **M3** | **Complete but for hardware** | PDFium render with a true region crop, the transform maths, whole-sheet composition against the *printable* area, GDI output, raw ZPL, printer profiles with calibration, printer discovery, and a recording device. Six render-diff cases against checked-in reference images. Printable geometry is read from a real installed driver in a test. **Not done:** the physical matrix on CITIZEN / 4BARCODE / ZEBRA and on A4 lasers — postponed by the customer to a joint session (section 10.2). |
| **M4** | **Complete but for capture** | Durable spool (idempotent intake, single-winner claim, lease-based recovery, backoff, poison queue), hot folders, job processor, work loop, fallback picker, Windows service host, tray and service/tray IPC. Soak: 30 documents across three worker lifetimes, nothing lost or duplicated. Picker measured on screen in 209-221 ms *in the foreground*. **Not done:** virtual-printer ingress, which is blocked on M1. The tray now actually runs: the executable's no-argument path - the one the installer's autostart entry and the Start Menu shortcut both take - constructed nothing and showed a usage message box, so the tray icon and the service's picker channel did not exist on an installed machine. It is covered by tests now, because the installed path was the only path nothing exercised. |
| **M5** | **Complete but for the worker** | Fleet schema (13 tables) and API, verified by running all 12 migrations from empty against real Postgres. Agent enrolment with a per-machine key, bundle sync with checksum verification and a 304 fast path, heartbeat, printer reporting, and job/trace/fallback reporting. All three decision modes implemented and tested, including server-unreachable behaviour for each. Bundles are validated at publish time against the shared schema, so a rule set neither engine could execute is a 400 rather than a fleet-wide outage. Retention runs on an advisory-locked schedule instead of only on a button. Every job reports its per-page outcome, its audit trail and — on a fallback — a thumbnail of the pages a person was asked about. **Not done:** the worker's own adoption of the shared engine — see section 10.3. |
| **M6** | **Complete** | A Fleet tab in the existing admin console - one login, one origin. Agents (decision mode, threshold, disable per machine), the rule bundle (an editor whose rejections name the exact failing rule path), fallback analytics (agreement rate and median decision time), the review queue where **one click derives a rule from the logged fallback**, **accounting with an explicit reconciliation**, and **recent jobs showing, per page, the printer it reached and the media it printed on with the layer that chose it**. A new carrier template is cut from a sample PDF in the browser - drag a box round the logo and get the template plus a matching rule. Driven in a real browser against a real database throughout. |
| **M7** | **Complete but for an elevated install** | Server: production Dockerfiles, a compose stack where only Traefik publishes a port, Traefik configured entirely from files, and `npm run smoke:prod`, which builds the real images and asserts the exit criterion end to end. Agent: a 48 MB self-contained WiX MSI with the service, the tray autostart, an ACL'd data directory and unattended properties; ADMX/ADML templates; a four-layer configuration reader with provenance (`--show-config`); GPO, signing and AV-exclusion procedures in `docs/DEPLOYMENT.md`. MSI contents verified by decompiling the package - service registration, `RemoveExistingProducts` at 6501 (after `InstallExecute`, before `InstallFinalize`), the data-directory ACL, all five registry values, 318 payload files, no debug symbols. The MSI now also lays down Start Menu shortcuts - `Printo` and `Printo Settings` - because a headless service plus an autostart entry that does not fire until the next sign-in reads as "nothing happened" to whoever ran the installer, which is exactly how it read. `Verify-Install.ps1` asserts both shortcuts and that the tray binary behind them exists; its old check looked only at the registry value, which is why the empty tray survived a release. **Not done:** actually installing it, which needs elevation. |
| **M8** | **Partly complete** | Docs: `docs/DEPLOYMENT.md` (server and agent, GPO, ADCS signing, AV exclusions) and `docs/MIGRATING_FROM_PRINT_AND_SHARE.md` (what maps onto what, how to run both at once, and what Printo does not do yet). CI: unchanged by request, and `npm run test`, `lint`, `typecheck`, `build` and the compose smoke all pass locally as CI runs them. **Not done:** the parts of the definition of done that need hardware or an elevated session - the printer matrix, an actual MSI install, and virtual-printer ingress. |

The GLS defect in M2's exit criteria turned out to be smaller and differently caused than the
plan assumed: 278 pages carry the `*GLS certified label*` footer, but only **4** were actually
mis-attributed, because DHL is tested first and wins on the other 274. The 4 are DHL
*DOMESTIC EXPRESS* labels, which matched none of the old DHL patterns (`dhl` does not
match `MyDHL`). Both the worker heuristic and the new engine now register the footer as the
DHL artifact it is and guard the GLS keyword against it.

### 10.8 The agent's own user interface

Three things were missing between "the MSI installed successfully" and a person being able to
use the product, and all three read to the operator as the same thing: nothing happened.

**The tray never started.** `Printo.Tray.exe` handled `--picker` and fell through everything
else to a usage message box, and `TrayApplication` - the notification icon, and the named pipe
the service raises the fallback picker through - was constructed nowhere. The installed
autostart entry pointed at a real executable that did nothing useful. The parsing is now a
separate, tested function, and the no-argument case is the first test in it.

**There was nothing to open.** The package created no shortcut of any kind, so a fresh install
offered no way in until the next sign-in, and even then only a tray icon. There are now two
Start Menu entries: `Printo`, which is the tray, and `Printo Settings`, which goes straight to
the settings window - because the first thing a new machine needs is its printers mapped, and
at that moment the tray has not started yet.

**There was nowhere to configure the machine.** Printer roles, media, calibration offsets and
watched folders existed only as JSON in a directory ordinary users cannot read. The settings
window edits exactly the things that are facts about *this machine* and nothing else:

| Tab | What it does |
|---|---|
| Printers | Maps installed Windows queues to `A4`, `THERMAL` or a rule-facing alias; media as a free `WxH mm` value; per-device calibration offset and zoom; raw ZPL with darkness and speed. A mapping to a queue that is no longer installed is shown in red here rather than discovered at print time from the poison queue. |
| Watched folders | Path, extensions, include/exclude masks, subfolders, what happens to the file afterwards, and the settle time that stops a half-written document being read. |
| General | Server address, decision mode, confidence threshold, OCR language, and where the data directory and configuration file are. |

Routing rules are deliberately not editable here. They are published centrally, validated
against both engines before release and shared by the whole fleet; thirty workstations each
with their own idea of what a DHL label looks like is the failure this product exists to end.

Two details that are not cosmetic:

- **Group Policy values are shown, not hidden.** A managed setting appears with its effective
  value, disabled, marked, and with a line saying where it is actually set. A helpdesk has to
  be able to see that a setting is wrong *and* that this is not the place to fix it. On save,
  managed settings are written back exactly as the file already held them, so withdrawing a
  policy actually withdraws it rather than leaving the imposed value baked into the file.
- **Saving elevates the smallest possible thing.** The data directory is ACL'd to SYSTEM and
  Administrators because the enrolment credential lives there, so an operator's tray cannot
  write the configuration. It stages the file and relaunches this same executable with
  `--apply`, which validates the staged JSON, copies it and restarts the service - and nothing
  else. Running the whole window elevated would be less code and much worse: a message loop, a
  PDF renderer and a printer enumerator behind the UAC prompt to change one JSON file.

**The calibration page** answers the questions the geometry depends on and that nothing but
hardware can answer: does the sheet the driver reports match the stock that is loaded, does the
printable area it reports match where the head can really mark, and is the device offset. It
draws the reported printable rectangle, millimetre rulers, a centre crosshair and the numbers
it was built from, composed exactly the way a real job is - a full-sheet 32bpp raster at device
resolution handed to the same `IPrinterDevice` - so a page that lands correctly is evidence
about the print path and not only about the drawing. It is how a site records a per-printer
offset without waiting for the hardware session in section 10.2.

### 10.2 What "M3 complete but for hardware" means

Everything up to the last inch is asserted automatically:

- the composed raster is the byte-identical buffer handed to `StretchDIBits`, so the
  render-diff references *are* what the driver receives;
- placement is computed against the printable area, and a test asserts content stays inside a
  laser's 4 mm dead zone;
- `WindowsPrinterDevice.Query` reads real DPI, physical size and unprintable offsets from an
  installed driver, exercising the DEVMODE round trip rather than a mock.

What no amount of test code can establish is whether a given printer marks the stock where
those numbers say it will. That needs the hardware session: print each render-diff case on
CITIZEN, 4BARCODE and ZEBRA at 100x150 and 100x200, and on the HP A4 units, measure the
result, and record any per-printer calibration offset in its `PrinterProfile`. Until then the
milestone is **not** reported as done.

### 10.3 Why the worker has not adopted the shared engine

The intent was that the worker and the agent execute one rule set. Measured against the
corpus, the worker cannot: **it has no rasterizer, and without an ink box the engine finds
no labels at all.**

Running the golden corpus with `inkBox` nulled and everything else intact:

| Input | Pages routed correctly |
|---|---|
| Full features | 1266 / 1266 (100.0%) |
| No ink box | 678 / 1266 (53.6%) |

The 588 failures are exactly the label pages — 423 FedEx, 145 DHL, 20 UPS — every one of
them routed to A4. That is not a degradation, it is a floor: 53.6% is precisely the share of
pages that are A4 anyway, so the engine contributes nothing without geometry. The reason is
structural and already recorded in section 1.5: labels in this corpus are *embedded regions*
on a carrier sheet, located by measurement, not whole pages identifiable from text.

The worker loads `pdfjs-dist` without `canvas`, so it has a text layer and page dimensions and
nothing else. Three ways forward, none of them free:

1. **Give the worker a rasterizer** (`@napi-rs/canvas` is prebuilt and needs no system
   packages, so it survives the Docker constraint). Costs CPU on every scanned page.
2. **Measure the ink box in the Vision Service**, which already rasterizes, and return it as a
   feature. Keeps the worker light; makes the engine path depend on a service.
3. **Leave the worker on its heuristic classifier**, which is what it does today, and accept
   that the two rule implementations are kept honest only by the conformance suite rather than
   by shared production traffic.

This is a real architectural choice rather than an oversight, so it is recorded here for a
decision rather than settled unilaterally. Nothing else in M5 depends on it.

### 10.4 What "a new carrier template, no code changes" does and does not mean

M6's exit criterion has two halves, and they landed differently.

**"A logged fallback converts to a rule in one click" - done, and verified end to end.** The
review queue derives a rule from the fallback's own trace: the measured ink box with an
explicit tolerance, plus the carrier when one resolved, routed to thermal with an `inkBox`
crop. It is stored on the review item with its rationale, adopted into the bundle editor on a
second click, and published on a third.

Publishing is deliberately *not* part of the one click. Pushing a machine-written rule to
thirty workstations without anyone reading it is how a fleet starts printing invoices on label
stock at four in the afternoon.

**"A new carrier template can be created end to end from a sample PDF" - done.** Open the
sample in the console, drag a box round the carrier's logo, name it, and the template and a
matching rule land in the bundle editor ready to publish. Rules and carrier signatures are
data, the validator rejects anything either engine could not execute and names the exact
failing path, and no code changes are involved at any point.

What the console still does not offer is authoring a *geometry* rule visually - dragging a
rectangle to set `inkWidthMm` and friends. Those are written as JSON, or derived automatically
from a logged fallback, which covers the case that actually arises.

### 10.7 Three loose ends closed

Auditing for dead ends - schema or API that exists and nothing uses - found three, all now
resolved rather than documented away.

**Page thumbnails.** `fallback_events.thumbnails_ref` existed with nothing to point at, and
section 11 confirms uploads are permitted with retention. The agent has already rendered every
page to decide about it, so on a fallback it sends a small copy of the pages in question; the
review queue shows them. Only on a fallback, and only the pages the picker asked about - a
thumbnail of every page of every job would put a picture of everything the company prints on
the server, which nothing needs. Stored as rows rather than in an object store so they inherit
the cascade and the retention sweep that already exist; an orphaned image nobody deletes is
worse than a slightly larger table.

**Job events.** `agent_job_events` and `ReportEventAsync` existed and nothing produced an event.
The agent already keeps this trail locally - which media layer was chosen, that the picker went
unanswered, that the machine printed on cached rules - and now uploads it with the job, so a
support question is answerable from the console instead of by reaching the workstation.

Both are strictly supplementary: the job has already landed when they are sent, and losing
either to a dropped connection must not make a reported job look unreported.

**`label_templates` is dropped** (migration 0013). Section 6.3 describes a LabelTemplate
library - per carrier and variant, how to detect the label and where its region sits. Every
part of that is already a page rule in the published bundle: `when` is the detection,
`then.transform.source` is the region, `then.transform.media` is the stock. Keeping both would
mean two answers to "how do I recognise a DHL label", which is exactly the drift the shared
conformance suite exists to prevent between the two engines. The library is a *view* of the
rules grouped by carrier, not a second store.

### 10.6 "The effective value and its source, visible on every job"

Media resolves through a five-layer precedence chain, and a rule usually names none of it. The
agent logged the resolved value and its layer to its own spool, so the answer to "why did that
label print at that size" existed only on the workstation.

Every printed page now reports the queue it reached, the media it printed on, the precedence
layer that supplied that media, and the resolution it was composed at - alongside, not instead
of, what the rule asked for. `GET /admin/agent-jobs/:id` returns it and the Fleet tab shows it
per page.

That also fixed a defect the change exposed: the reported printer queue was only filled in when
a job went to exactly one printer, so it was null for every mixed document - the case the
product exists for.

### 10.5 Picture matching

The `image` predicate was in the schema, validated and traced, while nothing could populate a
template match - so a rule using it validated cleanly and could never fire. That is worse than
an absent feature, because it looks configured.

It now works, through the same lazy two-phase protocol OCR uses: the engine reports which
templates it needs matched on which pages, the host - the only side holding the pixels - runs
the match, and evaluation repeats. Both engines implement the request side; only the agent
implements the matcher, which is exactly the OCR arrangement.

- **Normalised cross-correlation**, so a logo printed lightly on one head and heavily on another
  scores the same. A pixel-difference metric would reject the lighter one.
- **Scale is physical.** A template carries the dpi it was captured at, the page is rendered to
  match, and an 18 mm logo is looked for at 18 mm.
- **Coarse-to-fine.** Exhaustive NCC of a 40x48 template over an A4 page at 150 dpi is about
  4 billion operations and took **35 seconds** when the first implementation derived its scale
  factor from the template size alone. Deriving it from the *cost* instead - both images shrink
  in both axes, so work falls as the fourth power - brings the same search, finding the same
  peak at the same pixel, to well under a tenth of a second.
- **A result is recorded even when it scores badly, and even when the bundle is missing the
  picture.** Otherwise the engine cannot tell "scored 0.2" from "nobody looked", asks again on
  the second pass, and the job fails as a rule set that asked twice - blaming the rules for a
  missing image.

Four conformance fixtures pin the protocol on both engines; four end-to-end tests take a real
PDF through PDFium, a PNG cut from a rendering of it, the engine's request, the matcher and the
printer.

The console cuts the reference image: open a sample document, drag a box round the logo, and
get the template plus a matching rule in the bundle editor. The cutting is done in the browser,
so the sample never leaves the machine, and the crop is taken from the canvas at 150 dpi with
that resolution recorded alongside it.

A PNG cut that way is a checked-in fixture (`tests/templates/console-cut-logo.png`), and three
tests take it through the agent: decode, locate on the page it came from, and route. That is
not ceremony - the two ends produce different PNGs. A canvas emits 8-bit RGBA, and a decoder
that only handled what the agent's own encoder writes would pass every other test in the suite
and fail on the first template an administrator actually cut.

Each milestone is committed and pushed to `github.com/awsosi/printo` as it completes.

---

## 11. Confirmed decisions

1. **Signing** — internal **ADCS**-issued Authenticode certificate. Devices are domain-joined
   with GPO control, so the chain is trusted fleet-wide and AV exclusions are pushed centrally.
   MSIX (Tier 3) is unlocked where the OS supports it.
2. **Thermal media** — default **100x150 mm**, but the system is elastic: media is a free
   `WxH mm` value resolved through the precedence chain in section 7.4, settable centrally and
   overridable per agent and per printer. The DHL crop is 92x180 mm, so on 100x150 stock it
   scales with `contain`; on 100x200 it lands near 1:1.
3. **Uploads** — permitted, with configurable retention per data class (documents, thumbnails,
   traces, job history, audit).
4. **Existing SMB/CUPS/socket/IPP server paths are kept**, not scrapped, even where the agent
   supersedes them.
5. **Virtual printer name** — `Printo`.
6. **Fleet** — 20-30 domain-joined workstations. Server on-prem, delivered as
   `docker compose up -d` / `down` with a `.env` policy, internal HTTP, fronted by Traefik
   configured via the **file provider** (dynamic YAML, no docker labels).
7. **Fallback** — unknown or unreliable routing prompts the user with the keyboard-first page
   picker (section 6.7) rather than guessing; every trigger is logged with a full rule trace and
   the user's actual choice, and surfaces in admin fallback analytics.
8. **UI language** — English only. End users only ever meet the picker; admins and helpdesk use
   the web UI.

### Remaining open items

- `auto` mode confidence threshold: proposing **0.75** default, configurable per profile.
- Picker timeout: proposing **none** (job waits in the tray) rather than auto-resolving to A4.
- Whether the picker should also allow choosing *which* thermal printer when a machine has more
  than one — proposing no by default (role mapping decides), with a per-agent opt-in.

---

## 12. Definition of done

- Windows agent installs in one pass on Win10 22H2 and Win11, sets up its virtual printer,
  enrolls against the server, and prints correctly with no manual file editing.
- Every one of the 1266 corpus pages routes correctly, with and without a text layer.
- Outgoing labels print on thermal media at correct size, orientation and position — cropped
  from A4 where necessary — verified on CITIZEN, 4BARCODE and ZEBRA hardware.
- Invoices, return notes, waybill sheets and return labels print on A4 correctly.
- Hot-folder intake never duplicates and never loses a file across restarts and failures.
- Agent survives server outage; all three decision modes work; accounting reconciles.
- The fallback picker appears in under a second, is fully keyboard-driven, and every trigger is
  logged with a reason code, a complete rule trace and the user's choice.
- A new carrier template is configurable from the admin UI without a code change, and a logged
  fallback converts into a rule in one click.
- Media size and every other print setting are configurable centrally and overridable locally,
  with the effective value and its source visible on every job.
- Server comes up from a clean checkout with `docker compose up -d` behind Traefik's file
  provider, and down with `docker compose down`.
- No placeholders, no TODOs, no dead ends. CI green: lint, typecheck, unit, corpus,
  render-diff, integration, compose smoke.
