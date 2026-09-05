# PROMPT.md — resume instructions

> **You are being pointed at this file at the start of a fresh session.** Read it, read the
> documents it points to, then start work at the current milestone.
>
> **Lifecycle of this file:** delete it when every milestone in
> `docs/WINDOWS_CLIENT_PLAN.md` section 10 is complete and the Definition of Done in section 12
> is met. Until then, keep it accurate: after each milestone, update
> "Current position" below and strike through what is finished. Never leave it stale.

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
| `docs/WINDOWS_CLIENT_PLAN.md` | **The approved plan.** Corpus analysis, architecture, capture tiers, rule schema, fallback picker, milestones M1-M8, definition of done. This is the contract. |
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

1. **Only 72 of 159 outgoing labels sit on label-sized pages.** The rest are a 4x6-ish label
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
- [ ] **M1 — Capture spike — code written, BLOCKED on one elevated command (see below)**
- [x] M2 — Corpus + engine core (core complete; two gaps listed below)
- [ ] M3 — Print output ← **next**
- [ ] M4 — Agent runtime + fallback picker
- [ ] M5 — Server integration
- [ ] M6 — Admin UI
- [ ] M7 — Packaging + delivery
- [ ] M8 — Hardening

### Environment answers already given by the user (2026-09-05)

| Question | Answer |
|---|---|
| Win10 22H2 machine | **None available.** Build and self-test on Win11; keep Win10 paths behind capability detection and mark them explicitly unverified. |
| Physical printers | **Postponed.** Hardware verification will be a hybrid session with the user. Use virtual/render-diff proof until then; never report hardware-verified. |
| Code-signing cert | **Not available yet.** The user can issue one from ADCS when directed. Plan and parameterise signing; do not block on it. |
| CI | **Leave `.github/workflows/ci.yml` alone.** Do not add a Windows job. Keep the existing ubuntu pipeline green. |
| Dev stack | Docker Desktop compose locally; compose stays the mandatory server form factor. |

### M1 — what is done and what is blocked

Written, building and self-tested (`clients/windows/spike/`, plan section 5.0):
`Printo.Spike.Ipp` (a real IPP/1.1 printer with an IPP Everywhere attribute table, switchable
PDF/raster advertising, full request logging and PDL sniffing — verified against a Python IPP
client), `Printo.Spike.PipePort` (named pipe with a LocalSystem ACL), and
`Invoke-SpikePrinters.ps1` (creates/removes exactly three `Printo-Spike-*` queues, idempotent).

**Blocked:** `Add-Printer` needs elevation. From a standard-user session it fails with
`Access was denied` *before any IPP traffic reaches the endpoint*, so neither capture question
is answered. A UAC prompt was raised once and cancelled. To finish M1, run:

```
! powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','C:\Users\olek\Documents\code\si\printo\clients\windows\spike\scripts\Invoke-SpikePrinters.ps1','-Action','Add'"
```

then start `printo-spike-ipp.exe`, print to `Printo-Spike-IPP` from Chrome, read
`capture/ipp-session.jsonl`, record the answer in plan section 5.0, and run the script again
with `-Action Remove`. **No production code may assume an answer until this is done.**

### M2 — what landed, and the two gaps

Green: 1266/1266 corpus pages routed correctly in **both** text-layer modes; 67 conformance
fixtures pass on the TypeScript **and** C# engines; 0 pages attributed to GLS; `make lint` and
`make typecheck` clean.

Two honest gaps, both recorded in plan section 1.5:

1. **Barcode predicates are unvalidated against real barcodes.** The anonymiser destroyed
   them — only 4 of 1266 pages decode. Implemented and unit-tested, but unproven on real data.
   Needs either a few non-anonymised PDFs or an anonymiser that re-encodes valid barcodes.
2. **Template/picture matching has no extractor.** The `image` predicate is specified, wired
   and traced, but nothing populates `templateMatches` yet. Needed for Print&Share parity (M6).

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
