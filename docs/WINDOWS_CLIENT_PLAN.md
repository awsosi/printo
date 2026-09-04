# Windows Client + Routing Engine — Understanding & Delivery Plan

Status: `DRAFT — awaiting review`
Last updated: 2026-09-04

This document is the proposal for the next phase of `printo`: a Windows agent that replaces
Print&Share on the workstation, plus the server-side work needed to make routing genuinely
more reliable than picture matching.

---

## 1. What the input data actually looks like

Analysed the full corpus in `C:\Users\olek\Documents\code\si\printo-materials`:
**258 PDFs / 1266 pages** across `wtorek_anon` (124), `czwart_anon` (82), `sroda_anon` (52).

### 1.1 Page-type census

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
  in the corpus contains the literal string `*GLS certified label*` — so **278 pages are
  mis-attributed to GLS**. Keyword-soup scoring is not good enough.
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
| `SERVER_UNAVAILABLE` | `server` mode, server unreachable, policy is `prompt` |
| `RULE_HOLD` | A rule explicitly asked for confirmation |
| `CROP_IMPLAUSIBLE` | Crop region degenerate, off-page, or wildly off the expected aspect |
| `RENDER_FAILED` / `DECODE_FAILED` | PDF or barcode decode error |

Behaviour per reason is configurable per profile: `prompt` (default), `route` (fall through to
the A4 default), or `hold` (queue in the tray, no UI). Nothing is ever silently dropped.

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
