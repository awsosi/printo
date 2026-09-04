#!/usr/bin/env python3
"""Extract per-page routing features from the printo corpus.

This is the ground-truth feed for the routing engine. It produces exactly the feature
set both engine implementations consume - geometry, ink bounding box, text layer and
decoded barcodes - so the golden corpus and the conformance fixtures are derived from
measured data rather than from assumptions about the documents.

The output is one gzipped JSON record per page (JSONL), which keeps a 1266-page corpus
well under a megabyte and lets the test suites run without the source PDFs, which live
outside the repository.

Usage:
    python tools/corpus/extract_features.py <corpus-dir> --out tests/corpus/features.jsonl.gz

Requires: pypdfium2, numpy, zxing-cpp, Pillow.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import gzip
import io
import json
import math
import os
import sys
import traceback

import numpy as np
import pypdfium2 as pdfium

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

# Ink-bounding-box detection. 100 dpi locates a label region to about 0.25 mm and keeps
# the whole corpus fast; the numbers in docs/WINDOWS_CLIENT_PLAN.md section 1.2 come from
# this same setting, so they stay comparable.
BBOX_DPI = 100.0
INK_LEVEL = 200
NOISE_FRACTION = 0.002

# Barcode decoding. 200 dpi resolves Code 128 and PDF417 on a 4x6 label; pages where
# nothing is found are retried at 300 dpi before giving up, because MaxiCode and dense
# PDF417 on an A4-embedded label can fall below the decoder's module-size floor.
BARCODE_DPI_PRIMARY = 200.0
BARCODE_DPI_RETRY = 300.0

# OCR. 250 dpi reads the DHL template chrome ("*WAYBILL DOC*", "Not to be attached to
# package") reliably while keeping a page under ~2.5s. OCR runs only over the ink box of
# pages that could plausibly be a label: an A4 invoice needs no OCR to be recognised as
# a document, and skipping those halves the corpus run.
OCR_DPI = 250.0
OCR_CANDIDATE_WIDTH_MM = (55.0, 135.0)
OCR_CANDIDATE_ASPECT = (1.15, 2.8)

POINTS_PER_MM = 72.0 / 25.4

# Lazily constructed per worker process; the ONNX session is expensive to build.
_OCR_ENGINE = None


def mm(points: float) -> float:
    return round(points / POINTS_PER_MM, 2)


def ink_box(raster: np.ndarray, dpi: float) -> dict | None:
    """Bounding box of non-white content in millimetres, origin top-left."""
    mask = raster < INK_LEVEL
    row_ink = mask.sum(axis=1)
    col_ink = mask.sum(axis=0)
    row_floor = max(1, int(mask.shape[1] * NOISE_FRACTION))
    col_floor = max(1, int(mask.shape[0] * NOISE_FRACTION))
    rows = (row_ink > row_floor).nonzero()[0]
    cols = (col_ink > col_floor).nonzero()[0]
    if rows.size == 0 or cols.size == 0:
        return None

    scale = 25.4 / dpi
    x0 = float(cols[0]) * scale
    x1 = float(cols[-1] + 1) * scale
    y0 = float(rows[0]) * scale
    y1 = float(rows[-1] + 1) * scale
    width = x1 - x0
    height = y1 - y0
    return {
        "xMm": round(x0, 2),
        "yMm": round(y0, 2),
        "widthMm": round(width, 2),
        "heightMm": round(height, 2),
        "aspect": round(height / width, 3) if width else None,
        "coverage": round(float(mask.mean()), 4),
    }


def decode_barcodes(page, dpi: float) -> list[dict]:
    """Decode every barcode on the page, returning symbology, value and rect in mm."""
    import zxingcpp

    bitmap = page.render(scale=dpi / 72.0, grayscale=True)
    image = bitmap.to_pil()
    results = zxingcpp.read_barcodes(image, try_rotate=True, try_downscale=True)

    scale = 25.4 / dpi
    barcodes = []
    for result in results:
        position = result.position
        xs = [position.top_left.x, position.top_right.x,
              position.bottom_left.x, position.bottom_right.x]
        ys = [position.top_left.y, position.top_right.y,
              position.bottom_left.y, position.bottom_right.y]
        barcodes.append({
            "symbology": str(result.format).replace("BarcodeFormat.", ""),
            "value": result.text,
            "xMm": round(min(xs) * scale, 2),
            "yMm": round(min(ys) * scale, 2),
            "widthMm": round((max(xs) - min(xs)) * scale, 2),
            "heightMm": round((max(ys) - min(ys)) * scale, 2),
        })
    return barcodes


def ocr_region_key(rect: dict) -> str:
    """Must agree with `ocrRegionKey` in packages/routing-engine/src/features.ts.

    The rounding is written out rather than left to `format`, which rounds half to even
    while JavaScript's `toFixed` rounds half away from zero - a region measured at exactly
    x.x5 would otherwise key differently in the extractor and in the engine.
    """
    return ",".join(
        f"{math.floor(rect[field] * 10 + 0.5) / 10:.1f}"
        for field in ("xMm", "yMm", "widthMm", "heightMm"))


def is_ocr_candidate(box: dict | None) -> bool:
    """Only pages whose ink could be a shipping label are worth OCR-ing."""
    if not box or not box.get("aspect"):
        return False
    width_ok = OCR_CANDIDATE_WIDTH_MM[0] <= box["widthMm"] <= OCR_CANDIDATE_WIDTH_MM[1]
    aspect_ok = OCR_CANDIDATE_ASPECT[0] <= box["aspect"] <= OCR_CANDIDATE_ASPECT[1]
    return width_ok and aspect_ok


def ocr_ink_box(page, box: dict) -> dict | None:
    """Recognise the ink box and return it as one OCR region with per-line rects."""
    global _OCR_ENGINE  # noqa: PLW0603 - one engine per worker process, built on demand
    if _OCR_ENGINE is None:
        from rapidocr_onnxruntime import RapidOCR

        _OCR_ENGINE = RapidOCR()

    image = page.render(scale=OCR_DPI / 72.0).to_pil()
    px = OCR_DPI / 25.4

    left = max(0, int(box["xMm"] * px))
    top = max(0, int(box["yMm"] * px))
    right = min(image.width, int((box["xMm"] + box["widthMm"]) * px))
    bottom = min(image.height, int((box["yMm"] + box["heightMm"]) * px))
    if right <= left or bottom <= top:
        return None

    crop = np.array(image.crop((left, top, right, bottom)))
    results, _ = _OCR_ENGINE(crop)

    lines = []
    for entry in results or []:
        quad, text = entry[0], entry[1]
        xs = [point[0] for point in quad]
        ys = [point[1] for point in quad]
        lines.append({
            "text": text,
            "xMm": round(box["xMm"] + min(xs) / px, 2),
            "yMm": round(box["yMm"] + min(ys) / px, 2),
            "widthMm": round((max(xs) - min(xs)) / px, 2),
            "heightMm": round((max(ys) - min(ys)) / px, 2),
        })

    rect = {
        "xMm": box["xMm"],
        "yMm": box["yMm"],
        "widthMm": box["widthMm"],
        "heightMm": box["heightMm"],
    }
    return {
        "key": ocr_region_key(rect),
        "rect": rect,
        "text": "\n".join(line["text"] for line in lines),
        "lines": lines,
    }


def extract_document(path: str, corpus_dir: str, want_ocr: bool = True) -> list[dict]:
    relative = os.path.relpath(path, corpus_dir).replace(os.sep, "/")
    pdf = pdfium.PdfDocument(path)
    records = []
    try:
        page_count = len(pdf)
        for index in range(page_count):
            page = pdf[index]
            textpage = page.get_textpage()
            text = textpage.get_text_bounded() or ""
            textpage.close()

            width_mm = mm(page.get_width())
            height_mm = mm(page.get_height())

            grayscale = np.asarray(
                page.render(scale=BBOX_DPI / 72.0, grayscale=True).to_numpy())
            box = ink_box(grayscale, BBOX_DPI)

            barcodes = decode_barcodes(page, BARCODE_DPI_PRIMARY)
            barcode_dpi = BARCODE_DPI_PRIMARY
            if not barcodes:
                barcodes = decode_barcodes(page, BARCODE_DPI_RETRY)
                barcode_dpi = BARCODE_DPI_RETRY

            ocr_regions = []
            if want_ocr and is_ocr_candidate(box):
                region = ocr_ink_box(page, box)
                if region:
                    ocr_regions.append(region)

            records.append({
                "doc": relative,
                "pageNumber": index + 1,
                "pageCount": page_count,
                "pageWidthMm": width_mm,
                "pageHeightMm": height_mm,
                "orientation": "landscape" if width_mm > height_mm else "portrait",
                "rotation": page.get_rotation(),
                "text": text,
                "textCharCount": len(text),
                "inkBox": box,
                "barcodes": barcodes,
                "barcodeDpi": barcode_dpi if barcodes else None,
                "ocrRegions": ocr_regions,
            })
            page.close()
    finally:
        pdf.close()
    return records


def worker(args: tuple[str, str, bool]) -> tuple[str, list[dict], str | None]:
    path, corpus_dir, want_ocr = args
    try:
        return path, extract_document(path, corpus_dir, want_ocr), None
    except Exception:  # noqa: BLE001 - one bad file must not stop the corpus
        return path, [], traceback.format_exc()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("corpus_dir")
    parser.add_argument("--out", required=True, help="gzipped JSONL output path")
    parser.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 4) - 2))
    parser.add_argument("--no-ocr", action="store_true", help="skip the OCR pass")
    args = parser.parse_args()

    if not os.path.isdir(args.corpus_dir):
        print(f"not a directory: {args.corpus_dir}")
        return 2

    paths = []
    for root, _dirs, files in os.walk(args.corpus_dir):
        for name in sorted(files):
            if name.lower().endswith(".pdf"):
                paths.append(os.path.join(root, name))
    paths.sort()
    print(f"{len(paths)} documents, {args.jobs} workers")

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)

    pages = 0
    failures = 0
    done = 0
    all_records: list[dict] = []

    with concurrent.futures.ProcessPoolExecutor(max_workers=args.jobs) as pool:
        for path, records, error in pool.map(
                worker,
                [(path, args.corpus_dir, not args.no_ocr) for path in paths],
                chunksize=1):
            done += 1
            if error:
                failures += 1
                print(f"ERR {path}\n{error}")
                continue
            all_records.extend(records)
            pages += len(records)
            if done % 25 == 0 or done == len(paths):
                print(f"  {done}/{len(paths)} documents, {pages} pages")

    all_records.sort(key=lambda r: (r["doc"], r["pageNumber"]))
    with gzip.open(args.out, "wt", encoding="utf-8") as handle:
        for record in all_records:
            handle.write(json.dumps(record, ensure_ascii=False) + "\n")

    size = os.path.getsize(args.out)
    print(f"\nwrote {args.out} ({size / 1024:.0f} KiB) - {pages} pages, {failures} failures")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
