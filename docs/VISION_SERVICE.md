# Vision Service Contract

The Vision Service (`services/vision`) owns all heavy OCR / barcode / rasterization work so the
Node worker stays free of native dependencies. The worker talks to it over HTTP; when the service
is unreachable the worker falls back to its local text-layer heuristic classifier
(`apps/worker/src/classify/heuristic-classifier.ts`) so printing never stops.

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
| `WORKER_CLASSIFIER` | `heuristic` \| `vision` \| `auto` (default `auto` when URL set) |
| `WORKER_VISION_TIMEOUT_MS` | Per-page request timeout (default 10000) |

## Running

```bash
# heuristics only (no native deps)
pip install -r services/vision/requirements.txt
uvicorn app:app --port 6000 --app-dir services/vision

# full stack (rasterizer + barcodes + OCR)
docker build --build-arg BUILD_PROFILE=full -t printo-vision services/vision
docker run -p 6000:6000 printo-vision
```
