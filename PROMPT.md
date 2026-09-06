# PROMPT.md — resume instructions

> **You are being pointed at this file at the start of a fresh session.** Read it, read the
> documents it points to, then start work at the current milestone. It is written for whichever
> assistant picks the work up — Claude Code, Codex or a person — and assumes no memory of any
> earlier session.
>
> **Start here:** section 6 is where the project stands. Section 6a was the decision blocking
> the next milestone; it was **settled on 2026-09-06** — one rule set for both paths — and the
> evidence behind it is in the same section. Section 6b is a log of what the last sessions
> changed and why, including corrections worth carrying forward.
>
> **Lifecycle of this file:** delete it when every milestone in
> `docs/WINDOWS_CLIENT_PLAN.md` section 10 is complete and the Definition of Done in section 12
> is met. Until then, keep it accurate: after each milestone, update
> "Current position" below and strike through what is finished. Never leave it stale.
>
> **Last updated:** 2026-09-06, after the cost measurement described in section 6b, on
> `feat/windows-agent`.

---

## 1. The task

Finish `printo` end to end — a hybrid system replacing
[Print&Share](https://www.printandshare.info/): a central server (routing rules, OCR/vision,
operations, accounting) plus a Windows client that acts as a virtual printer and routes pages
of mixed documents to the right physical printers.

**No placeholders, no dead ends, no TODOs.** Everything implemented, tested and working.

The immediate objective is the **Windows client**: a lightweight, robust agent with a minimal
GUI and a Windows Service, easy to install and configure, that captures print jobs from the
current user (virtual printer) or from watched directories, and routes each page — outgoing
carrier labels to thermal printers, everything else to A4 printers — with correct margins,
zoom, scaling and orientation per printer.

## 2. Read these first, in this order

| Document | What it gives you |
|---|---|
| `docs/WINDOWS_CLIENT_PLAN.md` | **The approved plan.** Corpus analysis, architecture, capture tiers, rule schema, fallback picker, milestones M1-M8, definition of done. This is the contract. **Sections 5.0, 5.0a and 5.0b are the newest and most important: what the Windows print path does to a page, and what routing one costs — both measured.** |
| `docs/ARCHITECTURE.md` | Existing service boundaries and topology |
| `docs/PLAN.md` | Delivery state of the already-built server stack |
| `README.md` | Repo commands, compose, current feature set |
| `EXTAUTH_API.md` | External auth contract |

The plan was reviewed and approved by the user. Do not re-litigate its decisions; if you find a
genuine problem with one, say so in a sentence and keep building.

## 3. Ground truth about the input data

Sample PDFs (anonymised, real production shape) live **outside the repo** at
`C:\Users\olek\Documents\code\si\printo-materials` — 258 PDFs / 1266 pages across
`wtorek_anon`, `sroda_anon`, `czwart_anon`. Carriers: DHL Express, FedEx, UPS.

Reproduce the census at any time:

```bash
python tools/corpus/analyze_corpus.py "C:\Users\olek\Documents\code\si\printo-materials" \
    --bbox --json corpus-report.json
```

The three findings that drive the whole design — details and measurements in
`docs/WINDOWS_CLIENT_PLAN.md` section 1:

1. **Only 72 of 588 outgoing-label pages sit on label-sized pages.** The rest are a 4x6-ish label
   *region* embedded in an A4-landscape, Letter or custom page. The system must **crop, rotate
   and scale onto the thermal media** — routing a whole page is not enough. Page order varies
   across 25+ shapes, so page index is never a valid routing key.
2. **Geometry alone cannot work.** The DHL courier waybill sheet and the DHL parcel label have
   near-identical page size, ink box and logo band (`92.2x183.6` vs `91.9x180.3` mm at the same
   origin). Only content separates them. That is why picture matching misroutes today.
3. **The anonymiser added the FedEx/UPS text layer**; production originals are image-only.
   Every classifier test must run the corpus twice — normal and `--strip-text-layer` — and a
   rule set that only passes with the synthetic text layer is **failing**.

Also fix on the way through: `apps/worker/src/classify/heuristic-classifier.ts` matches
`/\bgls\b/i`, and every DHL label contains the literal `*GLS certified label*` — 278 pages are
currently mis-attributed to GLS.

## 4. Decisions already confirmed by the user — do not re-ask

| Topic | Decision |
|---|---|
| Client stack | C# / .NET 10 LTS, win-x64, self-contained, under `clients/windows/` |
| OS baseline | Windows 10 22H2 **and** Windows 11; installer runs as local admin |
| Decision split | Per-agent switchable `local` / `server` / `auto` — weak machines can offload to the server |
| Thermal routing | **Outgoing carrier label only.** Waybill sheet and return label stay on A4 |
| Configurability | Routing profiles editable like Print&Share: picture, text, OCR — plus barcode and geometry |
| Signing | Internal **ADCS** Authenticode cert; domain-joined fleet, GPO pushes trust chain + AV exclusions and deploys the MSI |
| Thermal media | Default **100x150 mm**, free `WxH mm` value, configurable centrally and overridable per agent and per printer |
| Uploads / retention | Uploads permitted; retention configurable per data class |
| Legacy paths | Keep the existing SMB / CUPS / socket / IPP server paths — additive, not a replacement |
| Virtual printer name | `Printo` |
| Fleet | 20-30 domain-joined workstations |
| Server delivery | On-prem `docker compose up -d` / `down`, `.env` policy, internal HTTP, **Traefik via the file provider — dynamic YAML, never docker labels** |
| Unknown pages | Prompt the user with the fallback page picker; never guess silently, never drop |
| UI language | **English only** |

### The fallback picker matters more than its size suggests

End users know only Ctrl+P then Enter in Chrome and are extremely fast. The picker
(`docs/WINDOWS_CLIENT_PLAN.md` sections 6.5-6.7) must not break that rhythm: centred on the
active monitor, focused, always-on-top, ~260 px thumbnails, no prose beyond one hint line,
`1`-`9` toggle / arrows / Space / **Enter prints** / **Esc = all A4**, likely-label pages
pre-selected. Ctrl+P to on-screen in under one second is a measured exit criterion, not an
aspiration.

Equally important: **every trigger must be logged with a reason code, the full rule trace
(which predicate failed, with the measured value) and what the user actually chose.** That is
how the admins drive the fallback rate down. A fallback that says only "routing failed" is a
defect.

## 5. Open items — proposed defaults, confirm when you first touch them

- `auto` mode confidence threshold: **0.75**, per-profile configurable.
- Picker timeout: **none** — the job waits in the tray rather than printing something wrong.
- Picker does **not** let the user choose which thermal printer when a machine has several
  (role mapping decides); per-agent opt-in if wanted.

## 6. Current position

**Branch:** `feat/windows-agent`

- [x] Corpus analysis and plan — approved (`docs/WINDOWS_CLIENT_PLAN.md`, `tools/corpus/`)
- [x] **M1 — Capture spike — ANSWERED. Tier 1 (IPP) works and delivers PDF. What it
      revealed is now the thing blocking everything else — read section 6a first.**
- [x] Agent UI — tray entry point, Start Menu shortcuts, settings window and
      calibration page (plan section 10.8)
- [x] M2 — Corpus + engine core (complete; two gaps listed below)
- [x] M3 — Print output (complete except the hardware pass, postponed by the user)
- [~] M4 — Agent runtime + fallback picker (all but virtual-printer ingress, which is now
      unblocked by M1 and blocked instead by the decision in section 6a)
- [~] M5 — Server integration (all but the worker's own engine adoption — plan §10.3)
- [x] M6 — Admin UI (Fleet tab; driven in a real browser against a real database)
- [~] M7 — Packaging + delivery (all but an elevated install of the MSI — see below)
- [~] M8 — Hardening (docs and migration guide done; the rest needs hardware or elevation)

### Environment answers already given by the user (2026-09-05)

| Question | Answer |
|---|---|
| Win10 22H2 machine | **None available.** Build and self-test on Win11; keep Win10 paths behind capability detection and mark them explicitly unverified. |
| Physical printers | **Postponed.** Hardware verification will be a hybrid session with the user. Use virtual/render-diff proof until then; never report hardware-verified. |
| Code-signing cert | **Not available yet.** The user can issue one from ADCS when directed. Plan and parameterise signing; do not block on it. |
| CI | **Leave `.github/workflows/ci.yml` alone.** Do not add a Windows job. Keep the existing ubuntu pipeline green. |
| Dev stack | Docker Desktop compose locally; compose stays the mandatory server form factor. |

### 6a. THE OPEN DECISION — read this before writing any code

**M1 is answered and it changed the problem.** The virtual printer works, but the Windows print
path transforms every page on its way through, and the shipped rule set does not survive the
transformation. Nothing about virtual-printer ingress should be built until this is decided.

Full evidence: `docs/WINDOWS_CLIENT_PLAN.md` sections 5.0 and 5.0a. Fixtures: `tests/capture/`.
Tests that pin it: `CaptureRoutingTests`. Reproduce with `Capture-Corpus.ps1` and
`tools/corpus/compare_captures.py`.

**What was measured** — 7 documents, one per page-shape family in the corpus census plus a page
of nothing but visible prose, printed from Chrome to an IPP Everywhere queue. 22 pages:

| Transformation | Pages | What it costs |
|---|---|---|
| Text layer removed | **22 of 22** | every `text` predicate |
| Landscape turned to portrait | 10 of 22 | `orientation`, `pageWidthMm`, `pageHeightMm` |
| Non-A4 media replaced by A4 | 5 of 22 | `pageIsLabelStock` and every page-size predicate |
| Images resampled to ~300 dpi | 4 of 22 | nothing; never resampled *up* |

The text result is not an artefact of the corpus being scans: the HTML probe was 368 characters
of ordinary prose with no images, and came back with nothing extractable. **Every printed job is
text-free.** OCR stops being an optimisation for scanned pages and becomes load-bearing.

**What survives is physical size.** Content is placed at 1:1, never scaled - `SCALED` appears
zero times in 22 pages. An ink box measuring 101.6x156.0 mm on disk measures 150.6x101.6 mm
printed: the same rectangle, turned.

**Result today: none of the seven documents routes a single page to thermal.** Six of them
contain an outgoing carrier label. Every page lands on A4, silently, because `onUnknown` is
`route` and the profile sets no `expectations.thermalPagesPerDocument`.

#### The decision

Rules for printed input must be stated in terms that survive printing: **ink box normalised for
orientation, plus content** - never the page frame, which on this path describes the *queue's
media* rather than the document.

A rotation-tolerant retry was considered and **rejected on the evidence**: transposing recovers
the three embedded FedEx/DHL labels whose rules key on an A4-landscape sheet, and neither the
label-stock page nor the UPS carrier sheet, because no transposition brings back a page size
that was substituted away. Do not re-propose it as a complete fix.

Geometry alone cannot finish either. The 22 measured ink boxes separate labels (99-102 mm short
edge) from courier waybill sheets (92-94 mm) by 5 mm, and plan section 1 already established
from the full corpus that the DHL waybill sheet and DHL parcel label sit at 92.2x183.6 and
91.9x180.3 mm - not separable by measurement at all. Content must discriminate, and on this
path content means OCR, barcodes or picture matching.

Two ways to organise it. **The user chose (1) on 2026-09-06** — one rule set for both paths,
keyed on the orientation-normalised ink box plus content, with page-frame predicates demoted to
optional corroboration. The corpus must be re-proven at 1266/1266 in both text-layer modes, and
the seven captures must route their labels to thermal. The user also approved making barcode
decoding lazy at the same time.

1. **One rule set for both paths**, keyed on ink box and content, page-frame predicates demoted
   to optional corroboration. Cleaner; but it re-derives rules that currently pass 1266/1266 and
   the corpus must be re-proven.
2. **A second profile for virtual-printer input**, leaving the corpus-calibrated profile to
   serve hot folders and files. Lower risk; two rule sets to keep true.

The assistant leaned toward (1) with (2) as the safe path, and (1) is what was chosen.

**The cost question is now answered, and it does not favour either shape.** Plan section 5.0b
measures every stage on all 22 captured pages (`CaptureCostTests`, opt-in). On an idle machine:
geometry 11 ms a page, OCR of the ink box 87 ms, one template match 133 ms — against **216 ms a
page for barcode decoding, which runs on every page unconditionally** because `AgentService`
builds the extractor with a `ZxingBarcodeDecoder`. Content-based routing is affordable; the
seven-page job pays about 1.6 s of always-paid cost. **Cost is not a reason to choose between
(1) and (2).**

Two things came out of it that any shape has to honour:

- **Barcode decoding is now lazy** (done 2026-09-06). It goes through the same two-phase request
  protocol as OCR and templates, so it runs only for the pages a rule asks about — taking the
  always-paid cost from 231 ms a page to about 11 ms.
- **The recogniser is orientation-sensitive and the print path turns pages.** A page reading 4
  characters upright reads 954 turned. The right turn is predicted by the ink box being wider
  than tall on **22 of 22** pages, so it costs nothing to derive — but OCR regions must be
  normalised for orientation *before* recognition, not only before geometry comparison.

### M1 — answered

Tier 1 wins. `Add-Printer -IppURL http://127.0.0.1:<port>/ipp/print` binds the inbox
**Microsoft IPP Class Driver** to our own endpoint: no driver to write, none to sign, no port
monitor, and Windows creates the port itself. It delivers **`application/pdf`**, IPP 2.0, via
`Validate-Job` / `Create-Job` / `Send-Document`, after nine `Get-Printer-Attributes` that must
all be answered or the queue will not bind. The job carries `job-name` (the original file name)
and `requesting-user-name` (`DOMAIN\user`) - both of which the spool and accounting wanted
anyway - plus copies, media-col, sides, colour mode, quality, orientation and resolution.

Embedded images arrive at native resolution rather than re-sampled, so barcode decoding and OCR
are no worse than on the source file. That was the main risk of this path and it did not
materialise.

Reproduce, from an **elevated** PowerShell (or double-click the `-Elevated.cmd` beside it):

```
powershell -ExecutionPolicy Bypass -File clients\windows\spike\scripts\Run-CaptureSpike.ps1
powershell -ExecutionPolicy Bypass -File clients\windows\spike\scripts\Capture-Corpus.ps1
```

Both scripts undo whatever a previous killed run left behind - default printer, queue, listener
- before starting, because `finally` does not run when a window is closed and a stranded run
leaves the machine's default printer pointing at a queue with nothing behind it.

`Printo.Spike.PipePort` and `Invoke-SpikePrinters.ps1` remain for the Tier 2 fallback, which is
no longer needed but is not deleted until the production virtual printer is proven.

### M2 — what landed, and the two gaps

Green: 1266/1266 corpus pages routed correctly in **both** text-layer modes; 67 conformance
fixtures pass on the TypeScript **and** C# engines; 0 pages attributed to GLS; `make lint` and
`make typecheck` clean.

Two honest gaps, both recorded in plan section 1.5:

1. **Barcode predicates are unvalidated against real barcodes.** The anonymiser destroyed
   them — only 4 of 1266 pages decode. Implemented and unit-tested, but unproven on real data.
   Needs either a few non-anonymised PDFs or an anonymiser that re-encodes valid barcodes.
2. **Picture matching has no authoring tool.** The matching itself works end to end - normalised
   cross-correlation against a template carried in the bundle, requested lazily through the same
   two-phase protocol as OCR (plan section 10.5). What is missing is a way to crop a reference
   image out of a sample PDF in the console; today it is produced by hand and pasted in as
   base64.

### M3 — what landed, and what is deliberately not claimed

`clients/windows/` now has five projects: `Printo.Agent.Core` (routing engine),
`.Render` (PDFium, rasters, PNG, feature extraction, barcodes), `.Printing` (GDI, raw ZPL,
profiles, discovery), `.Ocr` (inbox Windows recogniser), `.Tests` (81 tests).

Proven automatically: region cropping by render origin (asserted pixel-identical to the same
window of a full render), placement against the *printable* area, six render-diff cases with
checked-in references, ZPL encoding, the media precedence chain, printer discovery, and real
printable geometry read from an installed driver.

**Not claimed:** that any physical printer marks the stock where those numbers say. That is
the hardware matrix in plan section 10.2 and needs the joint session — CITIZEN, 4BARCODE and
ZEBRA at 100x150 and 100x200, plus the A4 lasers, printing each render-diff case, measuring,
and recording any per-printer offset in its `PrinterProfile`.

### M4 — what landed

`Printo.Agent.Runtime` (spool, hot folders, job processor, work loop, picker model, IPC,
configuration), `Printo.Agent.Service` (Windows service host), `Printo.Agent.Tray` (tray icon,
pipe server, picker window).

Verified:

- **Soak:** 30 documents across three worker lifetimes with the spool closed and reopened
  between them — every job completed, each accepted once, exactly 30 pages printed.
- **Crash recovery:** work stranded by a dead process is left alone while its lease holds and
  reclaimed once it expires.
- **Picker:** on screen in 209-221 ms against the 1 s criterion, on a real 6-page corpus
  document, verified *in the foreground* rather than merely created.
- **Service host:** run for real — accepts a drop, spools a copy, archives the original,
  routes, and records the failure with backoff when no printer is mapped.

Still missing from M4: **virtual-printer ingress**, which cannot be built until M1 answers
which capture tier works. Hot folders are the working intake path meanwhile.

### Verification commands

```bash
npm run lint && npm run typecheck                      # repo-wide, must stay green
npx vitest run --root packages/routing-engine          # 141 tests incl. golden corpus and picture matching
dotnet test clients/windows/Printo.Agent.Tests         # 224 tests incl. corpus parity, soak, captures
npm run smoke:prod                                     # builds the production images, asserts the stack
pwsh clients/windows/installer/build.ps1 -Version 0.1.0           # builds the agent MSI
Printo.Tray.exe --picker <document.pdf> [pages]        # measure the picker, prints timing
Printo.Tray.exe --settings                             # the settings window, standalone
python tools/corpus/compare_captures.py tests/capture/session   # what printing did to each page
PRINTO_MEASURE_COST=1 dotnet test clients/windows/Printo.Agent.Tests --filter CaptureCostTests                                                        # stage-by-stage cost; opt-in, ~2 min, run it idle
Printo.Agent.exe --console --config <agent.json>       # run the service in the foreground
npx tsx packages/routing-engine/scripts/export-profiles.ts        # after editing profiles.ts
npx tsx packages/routing-engine/scripts/export-corpus-fixtures.ts # after changing the engine
PRINTO_UPDATE_REFERENCES=1 dotnet test clients/windows/Printo.Agent.Tests  # accept new render output
```

The agent API tests need a migrated database and skip silently without one:

```bash
docker compose -f infra/docker-compose.yml up -d db
DATABASE_URL=postgres://printo:printo@127.0.0.1:5432/printo npm run migrate -w @printo/api
PRINTO_TEST_DATABASE_URL=postgres://printo:printo@127.0.0.1:5432/printo npm run test -w @printo/api
```

The corpus tests skip silently when `printo-materials` is absent; point `PRINTO_CORPUS_DIR`
at it or keep it beside the checkout.

### M5 — what landed, and the one thing that did not

Both halves are wired: the server stores the fleet and can decide a document from features
alone, and the agent enrols, syncs its rule bundle, decides in whichever of the three modes it
is configured for, and reports every job with its trace.

Two things are worth knowing before touching this code:

- **Only features cross the network, never the document.** `POST /agents/me/decide` takes
  measured page features and returns a per-page plan, so the workstation stays the only place
  a customer's invoice is rendered, and the request is kilobytes rather than megabytes. The
  two-phase OCR protocol survives the round trip because only the agent holds the pixels.
- **Bundles are validated at publish time** (`packages/routing-engine/src/wire.ts`), because
  the C# engine throws on a predicate key it does not know. Without that check a bad rule set
  would be accepted, pushed to every workstation, and fail at print time on all of them.

**Not done:** the worker still uses its own heuristic classifier. This is measured, not
forgotten — with no rasterizer the worker has no ink box, and without an ink box the shared
engine routes 678/1266 corpus pages, missing *every* label. Plan §10.3 has the numbers and the
three ways forward; it needs a decision, and nothing else depends on it.

## 6b. Session log

### 2026-09-06 (later session) — the cost of routing a printed page

Answered the question section 6a had left open: measure before choosing. `CaptureCostTests` times
every stage on all 22 captured pages and is written up as plan section **5.0b**. It is opt-in
behind `PRINTO_MEASURE_COST` because it is a benchmark — two minutes of CPU-saturating work — and
benchmarks do not belong in a suite that is run for correctness.

The headline is that the assumption behind the question was wrong. OCR is not the expensive
stage: 87 ms for the ink box, against **216 ms a page for barcode decoding, which runs
unconditionally on every page**. Making barcodes lazy — exactly as OCR and picture matching
already are — is worth more than every OCR call in a job. Cost does not decide between the two
rule-set shapes in 6a.

A second finding came out of the numbers rather than being looked for. OCR of the ink box was
returning *less* text than OCR of the whole page, which should be impossible. The cause is that
the recogniser decides text orientation from the bitmap it is handed, and the print path turns 10
of the 22 pages: one page yields 4 characters upright and 954 turned. The correct turn is
predicted by the ink box measuring wider than tall on 22 of 22 pages, so it is free to derive —
and a test now asserts that, because searching all four turns costs seven times as much and finds
the same answer.

Two test-suite defects were found and fixed on the way, both pre-existing and both exposed rather
than caused by adding a test class:

- **`TrayPipeServer.Start()` returned before its pipe existed.** It queued the listener on the
  thread pool, so on a busy machine a caller could fail to connect to a server it had just
  started. `TrayIpcTests` failed **5 runs out of 5** at baseline on this machine, which
  contradicts the "222 green" this file previously claimed. `Start` now waits for the pipe and
  the listener runs on its own thread rather than behind arbitrary pool work — a user waiting on
  the picker should not queue behind background tasks. 0 failures in 5 runs after.
- **The picker's 400 ms latency assertion could not be honestly measured in-suite.** PDFium
  serialises every render in the process behind one global lock, and the rest of the suite
  renders constantly, so the stopwatch mostly measured contention. Retrying does not fix a lock
  held longer than the test runs. It is now a loose regression guard, with the real criterion
  left where it means something — `Printo.Tray.exe --picker` on an idle machine, the 209-221 ms
  already recorded.

Suite: **224/224, green over six consecutive runs.** `npm run lint` and `npm run typecheck` clean.

Still not done: virtual-printer ingress, still blocked on the 6a decision — which is now down to
one question, since cost no longer bears on it.

### 2026-09-06 (earlier session)

Started from "I installed the MSI and no app came up". That turned out to be three defects and
one wrong diagnosis, and then the M1 answer changed the shape of the project.

**The MSI was invisible, and partly broken.** The install had in fact succeeded and been
uninstalled three minutes later. But `Printo.Tray.exe` run with no arguments - the path the
autostart entry and every workstation take - fell through to a usage message box, and
`TrayApplication`, which owns the notification icon *and* the named pipe the service raises the
fallback picker through, was constructed nowhere in the repository. The package also created no
shortcut of any kind. Fixed: the argument parsing is a tested function whose first test is the
no-argument case; two Start Menu shortcuts; and `Verify-Install.ps1` now asserts the shortcuts
and the binary behind them rather than only a registry value, which is how the empty tray
survived a release.

**A settings window** (plan section 10.8) - printers with roles, media, calibration and raw ZPL;
watched folders; server, decision mode, threshold, OCR language. Group Policy values are shown
disabled and marked, and written back unchanged so withdrawing a policy withdraws it. Saving
stages the file and relaunches elevated with `--apply` - validate, copy, restart service, and
nothing else - because the data directory is ACL'd to SYSTEM and Administrators. A test caught
that path blanking a working configuration when the staged file was missing, since `Load` treats
absence as a fresh machine. A calibration page prints rulers and the driver's reported geometry,
composed exactly as a real job is.

**M1 answered** (section 6a above). Along the way the capture harness itself needed hardening:
the repository root was computed by climbing three directories instead of four, which put a
session under `clients\tests\` and made a run that *had* happened look like one that had not -
the assistant reported "it never ran" on that basis and was wrong. Both scripts now assert the
root against a landmark, kill orphaned listeners, and undo a previous run's default printer from
a breadcrumb in ProgramData, because `finally` does not run when a window is closed.

**Not done, deliberately:** virtual-printer ingress. It is the next milestone and it is blocked
on the decision in 6a, not on effort.

### Corrections worth carrying forward

- The user asked for evidence before committing to a fix, over the assistant's proposal to fix
  it from one sample. They were right: the multi-document capture invalidated the proposal.
- The user cares about efficiency and reliability as first-class constraints and has said the
  stack must be elastic to input formats it cannot control. "Retry" as a word landed badly and
  needed disambiguating from I/O retry; measure costs rather than assert they are small.
- English only in the UI. The machine is Polish-locale, so tool output is often Polish - test
  summaries read `Powodzenie` for pass and `Niepowodzenie` for fail.

## 7. Working agreements

- Work on `feat/windows-agent`. Commit and push to `https://github.com/awsosi/printo` as each
  milestone completes, and at sensible points within a milestone.
- Every milestone must meet its exit criteria in `docs/WINDOWS_CLIENT_PLAN.md` section 10
  before moving on. Report failures with the actual output; never report a milestone done that
  is not verified.
- Keep `make lint`, `make typecheck`, `make test` green. CI is `.github/workflows/ci.yml`.
- Match the surrounding code's style, comment density and naming. The TypeScript uses explicit
  interfaces, `.js` ESM import suffixes and doc comments explaining *why*.
- Do not scrap working server functionality (SMB scanning, CUPS/socket/IPP dispatch, auth,
  i18n, audit) while adding the agent path.
- Update `docs/PLAN.md` and `README.md` as capabilities land, and keep this file's
  "Current position" current.
