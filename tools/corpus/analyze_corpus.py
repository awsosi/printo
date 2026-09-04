#!/usr/bin/env python3
"""Profile the printo label corpus.

Produces the page census, per-page geometry and the ink bounding boxes that the
routing engine's geometry predicates are calibrated against. This is the script
that produced the numbers quoted in docs/WINDOWS_CLIENT_PLAN.md section 1.

Usage:
    python tools/corpus/analyze_corpus.py <corpus-dir> [--json out.json] [--bbox]

    <corpus-dir>  directory containing the sample PDFs (searched recursively)

Requires: pypdfium2, numpy (numpy only for --bbox).
"""

from __future__ import annotations

import argparse
import collections
import io
import json
import os
import re
import sys

import pypdfium2 as pdfium

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

# Rasterization DPI for ink-bounding-box detection. 100 is enough to locate a
# label region to ~0.25mm and keeps a 1266-page corpus under a minute.
BBOX_DPI = 100.0
# Ink threshold on the 8-bit grayscale render; anything darker counts as content.
INK_LEVEL = 200
# Ignore rows/columns with less than this fraction of ink, so speckle and
# scanner noise do not inflate the box.
NOISE_FRACTION = 0.002

PAGE_KIND_RULES = [
    ("DHL_WAYBILL_DOC", re.compile(r"\*WAYBILL DOC\*|Not to be attached to package", re.I)),
    ("INVOICE", re.compile(r"Sales Invoice", re.I)),
    ("RETURN_NOTE", re.compile(r"Return Note", re.I)),
    ("DHL_LABEL", re.compile(r"EXPRESS WORLDWIDE|MyDHL", re.I)),
    ("CUSTOMS_DECLARATION", re.compile(r"Council Regulation \(EC\)", re.I)),
]

CARRIER_PATTERNS = {
    # NOTE: a bare \bGLS\b is deliberately absent. Every DHL MyDHL label in the
    # corpus contains the literal "*GLS certified label*", which is what makes
    # the current worker heuristic mis-attribute 278 pages to GLS.
    "DHL": re.compile(r"\bDHL\b|EXPRESS WORLDWIDE|MyDHL", re.I),
    "UPS": re.compile(r"\bUPS\b|1Z[0-9A-Z]{16}", re.I),
    "FEDEX": re.compile(r"Fed\s?Ex", re.I),
    "DPD": re.compile(r"\bDPD\b", re.I),
    "INPOST": re.compile(r"InPost|Paczkomat", re.I),
    "POCZTA_POLSKA": re.compile(r"Poczta Polska|Pocztex", re.I),
}


def mm(points: float) -> float:
    return round(points / 72.0 * 25.4, 1)


def classify_page(text: str) -> str:
    for kind, pattern in PAGE_KIND_RULES:
        if pattern.search(text):
            return kind
    return "LOW_TEXT_LIKELY_LABEL_IMAGE" if len(text) < 600 else "OTHER"


def detect_carrier(text: str) -> str | None:
    for carrier, pattern in CARRIER_PATTERNS.items():
        if pattern.search(text):
            return carrier
    return None


def ink_bbox(page) -> dict | None:
    """Bounding box of non-white content, in PDF points, origin top-left."""
    import numpy as np

    raster = np.asarray(page.render(scale=BBOX_DPI / 72.0, grayscale=True).to_numpy())
    mask = raster < INK_LEVEL
    row_ink = mask.sum(axis=1)
    col_ink = mask.sum(axis=0)
    row_floor = max(1, int(mask.shape[1] * NOISE_FRACTION))
    col_floor = max(1, int(mask.shape[0] * NOISE_FRACTION))
    rows = (row_ink > row_floor).nonzero()[0]
    cols = (col_ink > col_floor).nonzero()[0]
    if rows.size == 0 or cols.size == 0:
        return None

    x0 = cols[0] / BBOX_DPI * 72.0
    x1 = (cols[-1] + 1) / BBOX_DPI * 72.0
    y0 = rows[0] / BBOX_DPI * 72.0
    y1 = (rows[-1] + 1) / BBOX_DPI * 72.0
    width, height = x1 - x0, y1 - y0
    return {
        "xMm": mm(x0),
        "yMm": mm(y0),
        "widthMm": mm(width),
        "heightMm": mm(height),
        "aspect": round(height / width, 2) if width else None,
    }


def analyze(corpus_dir: str, want_bbox: bool) -> list[dict]:
    documents = []
    for root, _dirs, files in os.walk(corpus_dir):
        for name in sorted(files):
            if not name.lower().endswith(".pdf"):
                continue
            path = os.path.join(root, name)
            try:
                pdf = pdfium.PdfDocument(path)
            except Exception as error:  # noqa: BLE001 - report and keep going
                print(f"ERR {path}: {error}")
                continue

            record = {
                "file": os.path.relpath(path, corpus_dir).replace(os.sep, "/"),
                "pageCount": len(pdf),
                "pages": [],
            }
            for index in range(len(pdf)):
                page = pdf[index]
                textpage = page.get_textpage()
                text = textpage.get_text_bounded() or ""
                entry = {
                    "pageNumber": index + 1,
                    "widthMm": mm(page.get_width()),
                    "heightMm": mm(page.get_height()),
                    "rotation": page.get_rotation(),
                    "textChars": len(text),
                    "kind": classify_page(text),
                    "carrier": detect_carrier(text),
                }
                if want_bbox:
                    entry["inkBox"] = ink_bbox(page)
                record["pages"].append(entry)
                textpage.close()
                page.close()
            pdf.close()
            documents.append(record)
    return documents


def summarize(documents: list[dict]) -> None:
    kinds = collections.Counter()
    sizes = collections.Counter()
    shapes = collections.Counter()
    carriers = collections.Counter()
    page_total = 0

    for document in documents:
        shape = "".join(
            "L" if page["widthMm"] > page["heightMm"] else "P" for page in document["pages"]
        )
        shapes[shape] += 1
        for page in document["pages"]:
            page_total += 1
            kinds[(page["kind"], f"{page['widthMm']}x{page['heightMm']}")] += 1
            sizes[(page["widthMm"], page["heightMm"])] += 1
            if page["carrier"]:
                carriers[page["carrier"]] += 1

    print(f"documents: {len(documents)}   pages: {page_total}\n")
    print("--- page kind x page size ---")
    for (kind, size), count in kinds.most_common():
        print(f"{count:5}  {kind:32} {size}")
    print("\n--- page sizes (mm) ---")
    for (width, height), count in sizes.most_common():
        print(f"{count:5}  {width} x {height}")
    print("\n--- carriers (pages) ---")
    for carrier, count in carriers.most_common():
        print(f"{count:5}  {carrier}")
    print("\n--- document page-shape signatures (P=portrait L=landscape) ---")
    for shape, count in shapes.most_common(15):
        print(f"{count:5}  {shape}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("corpus_dir")
    parser.add_argument("--json", dest="json_out", help="write the full report here")
    parser.add_argument("--bbox", action="store_true", help="compute ink bounding boxes (slower)")
    args = parser.parse_args()

    if not os.path.isdir(args.corpus_dir):
        print(f"not a directory: {args.corpus_dir}")
        return 2

    documents = analyze(args.corpus_dir, args.bbox)
    summarize(documents)

    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8") as handle:
            json.dump(documents, handle, ensure_ascii=False, indent=2)
        print(f"\nwrote {args.json_out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
