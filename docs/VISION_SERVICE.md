# Vision Service Contract

The Vision Service (`services/vision`) owns all heavy OCR / barcode / rasterization work so the
Node worker stays free of native dependencies. The worker talks to it over HTTP; when the service
is unreachable the worker falls back to its local text-layer heuristic classifier
(`apps/worker/src/classify/heuristic-classifier.ts`) so printing never stops.

It has two jobs, and `/v1/page-features` is the important one: it **measures** a page for the
shared routing engine, which is what lets the server path run the same rules as the Windows
agent. `/v1/classify-page` is the older self-contained classifier, kept for deployments that put
a trained model behind that endpoint.

## Build profiles

The image is built three ways, and the choice decides what the server can do:

| `BUILD_PROFILE` | Contains | `/v1/page-features` |
|---|---|---|
| `minimal` | text heuristics only | **503** — the worker falls back to its own classifier |
| `geometry` (default) | pypdfium2, numpy, zxing-cpp, Pillow | geometry, ink box, barcodes |
| `full` | `geometry` plus PaddleOCR | the above plus OCR of requested rectangles |

`geometry` is the default because **without an ink box the shared engine finds no labels at
all** — 678 of 1266 corpus pages, missing every one of the 588 labels, because labels in this
corpus are regions embedded on a carrier sheet rather than whole pages. `full` adds most of a
gigabyte and is only needed by rule sets whose predicates read the page.

## Endpoints

### `GET /health`

```json
{
  "service": "vision",
  "status": "ok",
  "backends": { "pdf_rasterizer": true, "barcodes": true, "ocr": false }
}
```

`backends` reports which optional layers are installed (see `requirements-full.txt`).

### `POST /v1/page-features`

Measures one page for the routing engine (`packages/routing-engine`). Everything is millimetres
with a top-left origin, which is the engine's contract; PDF points never cross this boundary.

Request (JSON):

| Field | Type | Notes |
|---|---|---|
| `page_number` | int | 1-based index within the source document |
| `page_count` | int | Pages in the document, for document-level rules |
| `page_pdf_base64` | string | **Required.** Single-page PDF; the service rasterizes it |
| `text` | string \| null | The caller's already-extracted text layer, passed through untouched |
| `barcodes` | bool | Decode barcodes on this page. Opt-in — see below |
| `ocr_regions` | rect[] | Rectangles to read, as `{x_mm, y_mm, width_mm, height_mm}` |

Response (JSON): `page_number`, `page_width_mm`, `page_height_mm`, `orientation`, `rotation`,
`text`, `ink_box` (`{x_mm, y_mm, width_mm, height_mm, aspect, coverage}` or `null` on a blank
page), `barcodes` (`null` when none were asked for, `[]` when the page was scanned and had
none — the engine treats those differently), `ocr_regions`, and `backends`.

Returns **503** when the image was built without a rasterizer. That is a deployment fact rather
than a document problem, and the worker treats it as a reason to fall back, not to fail the job.

**Barcodes and OCR are opt-in on purpose.** The engine asks for them only for the pages a rule
needs them for — measured on the agent at 216 ms a page for decoding against 11 ms for geometry
(plan section 5.0b) — and this endpoint follows the same two-phase protocol: measure, evaluate,
ask for exactly what is missing, evaluate again.

**The measurements are calibrated, not tuned.** `services/vision/features.py` uses the same
resolution (100 dpi), ink level (200) and noise floor (0.002) as `tools/corpus/extract_features.py`,
which produced the corpus the rules were written against. Measuring differently would route
differently from the Windows agent while both sides ran identical rules — the hardest defect to
see, because each side alone looks correct. `tools/corpus/check_vision_features.py` runs the
production measuring code over the corpus PDFs and compares it page for page; it currently
agrees on all 1266.

### `POST /v1/classify-page`

Request (JSON):

| Field | Type | Notes |
|---|---|---|
| `page_number` | int | 1-based page index within the source document |
| `text` | string \| null | Extracted PDF text layer; `null`/empty for scans |
| `page_width` / `page_height` | float \| null | Page size in PDF points (1/72 in) |
| `page_pdf_base64` | string \| null | Single-page PDF; service rasterizes at ~200 dpi via pypdfium2 |
| `image_png_base64` | string \| null | Pre-rendered page bitmap, takes precedence over the PDF |

Response (JSON):

| Field | Type | Notes |
|---|---|---|
| `page_class` | enum | `OUTGOING_LABEL_THERMAL` \| `RETURN_LABEL_A4` \| `DOCUMENT_A4` |
| `confidence` | float 0..1 | Confidence in `page_class` |
| `carrier` | string \| null | `DHL`, `UPS`, `FEDEX`, `DPD`, `GLS`, `INPOST`, `POCZTA_POLSKA`, … |
| `is_return` | bool | Return-label markings detected |
| `barcodes` | array | `{symbology, value, bounding_box{x,y,width,height}}` in image pixels |
| `evidence` | string[] | Machine-readable reasons (`carrier:dhl`, `keyword:ship to`, `barcodes:2`, `ocr:paddle`, …) |

Errors: non-2xx status. The worker treats any error/timeout as "fall back to heuristic".

## Classification layers

1. **Text heuristics** — carrier signatures, label/return/document keywords, tracking-number
   patterns, label-sized page detection. Deterministic; mirrored in the worker so CI can run
   without the service.
2. **Rasterization** (`pypdfium2`) — renders `page_pdf_base64` when a bitmap is needed.
3. **Barcode detection** (`zxing-cpp`) — Code 128/GS1-128, MaxiCode, DataMatrix, PDF417, ITF,
   Code 39. Logistics symbologies raise the label score; MaxiCode implies UPS.
4. **OCR** (PaddleOCR) — only for pages without a text layer; recovered text is fed back into
   layer 1.

Swapping in a trained model (YOLO/RF-DETR shipping-label detector, carrier classifier) means
reimplementing `classify()` behind the same response schema — the worker does not change.

## Worker configuration

| Env var | Meaning |
|---|---|
| `WORKER_VISION_URL` | Base URL, e.g. `http://vision:6000`. Unset → heuristic only |
| `WORKER_CLASSIFIER` | `engine` \| `heuristic` \| `vision` \| `auto` (default `engine` when URL set) |
| `WORKER_VISION_TIMEOUT_MS` | Per-page request timeout for `/v1/classify-page` (default 10000) |
| `WORKER_VISION_FEATURE_TIMEOUT_MS` | Timeout for `/v1/page-features`, which rasterizes (default 20000) |

`engine` runs the shared routing engine with this service measuring each page, and falls back to
the heuristic classifier when the service is unreachable, when a rule needs a picture template
(those live in the agent's bundle, not here), or when the rules will not settle. `vision` and
`auto` are the pre-engine behaviours, kept so a site that pinned one gets no surprises.

## Running

```bash
# heuristics only (no native deps)
pip install -r services/vision/requirements.txt
uvicorn app:app --port 6000 --app-dir services/vision

# full stack (rasterizer + barcodes + OCR)
docker build --build-arg BUILD_PROFILE=full -t printo-vision services/vision
docker run -p 6000:6000 printo-vision
```
