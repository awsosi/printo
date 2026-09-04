#!/usr/bin/env python3
"""Bootstrap the golden-corpus ground truth.

Assigns every page a class and an expected route, and records the evidence behind each
decision so the one-off human review is a scan for wrong rows rather than 1266 judgements.

This is deliberately *not* the routing engine. It uses every signal at once - text layer,
OCR, page size and ink geometry - with no laziness, no confidence thresholds and no rule
ordering. The engine then has to reproduce these answers under the real constraints
(lazy OCR, and again with the text layer stripped), which is what makes the golden test
meaningful rather than circular.

Usage:
    python tools/corpus/label_corpus.py tests/corpus/features.jsonl.gz \\
        --out tests/corpus/expected.json
"""

from __future__ import annotations

import argparse
import collections
import gzip
import io
import json
import re
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

THERMAL = "THERMAL"
A4 = "A4"

# Courier-copy markings. Present as text on most DHL sheets and, on the template variant
# the anonymiser flattened, only as pixels - which is why OCR is consulted too.
WAYBILL_MARKERS = re.compile(
    r"WAYBILL\s*DOC|Not\s*to\s*be\s*attached|Hand\s*to\s*Courier", re.I)

DHL_PRODUCT = re.compile(r"EXPRESS\s*WORLDWIDE|ECONOMY\s*SELECT|MyDHL", re.I)
INVOICE = re.compile(r"Sales\s+Invoice", re.I)
RETURN_NOTE = re.compile(r"Return\s+Note", re.I)
CUSTOMS = re.compile(r"Council\s+Regulation|\bY9\d\d\b", re.I)


def page_text(record: dict) -> str:
    """Text layer plus OCR, whitespace-collapsed. Ground truth may use everything."""
    parts = [record.get("text") or ""]
    for region in record.get("ocrRegions") or []:
        parts.append(region.get("text") or "")
    return re.sub(r"\s+", " ", " ".join(parts))


def squashed(text: str) -> str:
    """Whitespace removed, for OCR output that loses the spaces in bold headers."""
    return re.sub(r"\s+", "", text)


def is_a4_landscape(record: dict) -> bool:
    return (290 <= record["pageWidthMm"] <= 305) and (200 <= record["pageHeightMm"] <= 220)


def is_a4_portrait(record: dict) -> bool:
    return (205 <= record["pageWidthMm"] <= 215) and (290 <= record["pageHeightMm"] <= 305)


def is_letter(record: dict) -> bool:
    return (210 <= record["pageWidthMm"] <= 220) and (272 <= record["pageHeightMm"] <= 288)


def is_ups_sheet(record: dict) -> bool:
    return (225 <= record["pageWidthMm"] <= 240) and (310 <= record["pageHeightMm"] <= 326)


def is_label_stock(record: dict) -> bool:
    return record["pageWidthMm"] <= 130 and record["pageHeightMm"] <= 260


def classify(record: dict) -> tuple[str, str, list[str]]:
    """Return (pageClass, expectedRoute, evidence)."""
    text = page_text(record)
    flat = squashed(text)
    box = record.get("inkBox")
    aspect = box["aspect"] if box and box.get("aspect") else 0.0
    width = box["widthMm"] if box else 0.0
    evidence: list[str] = []

    waybill = bool(WAYBILL_MARKERS.search(text)) or bool(
        WAYBILL_MARKERS.search(flat)) or "WAYBILLDOC" in flat.upper()
    if waybill:
        evidence.append("marker:waybill-doc")

    if is_label_stock(record):
        evidence.append(f"page:label-stock {record['pageWidthMm']}x{record['pageHeightMm']}")
        if waybill:
            return "DHL_WAYBILL_DOC", A4, evidence
        return "DHL_LABEL", THERMAL, evidence

    if is_ups_sheet(record):
        evidence.append("page:ups-carrier-sheet")
        return "UPS_LABEL", THERMAL, evidence

    if is_letter(record):
        evidence.append("page:letter")
        return "FEDEX_RETURN_LABEL", A4, evidence

    if is_a4_landscape(record):
        evidence.append(f"page:a4-landscape ink-aspect {aspect:.2f} width {width:.1f}")
        if waybill:
            return "DHL_WAYBILL_DOC", A4, evidence
        if 1.35 <= aspect <= 1.70:
            evidence.append("geometry:4x6-label")
            return "FEDEX_LABEL", THERMAL, evidence
        if 1.75 <= aspect <= 2.20:
            if DHL_PRODUCT.search(text):
                evidence.append("marker:dhl-product")
            return "DHL_LABEL", THERMAL, evidence
        return "UNKNOWN_A4_LANDSCAPE", A4, evidence

    if is_a4_portrait(record):
        if INVOICE.search(text):
            evidence.append("marker:sales-invoice")
            return "INVOICE", A4, evidence
        if RETURN_NOTE.search(text):
            evidence.append("marker:return-note")
            return "RETURN_NOTE", A4, evidence
        if CUSTOMS.search(text):
            evidence.append("marker:customs-declaration")
            return "CUSTOMS_DECLARATION", A4, evidence
        if box is None or box["heightMm"] < 60:
            evidence.append("geometry:sparse-portrait-page")
            return "SIGNATURE_PAGE", A4, evidence
        evidence.append("geometry:a4-portrait")
        return "DOCUMENT", A4, evidence

    evidence.append(f"page:{record['pageWidthMm']}x{record['pageHeightMm']}")
    return "UNKNOWN", A4, evidence


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("features")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    records = [
        json.loads(line)
        for line in gzip.open(args.features, "rt", encoding="utf-8")
        if line.strip()
    ]

    entries = []
    classes: collections.Counter[str] = collections.Counter()
    routes: collections.Counter[str] = collections.Counter()
    unknown = []

    for record in records:
        page_class, route, evidence = classify(record)
        classes[page_class] += 1
        routes[route] += 1
        entry = {
            "doc": record["doc"],
            "pageNumber": record["pageNumber"],
            "pageClass": page_class,
            "route": route,
            "evidence": evidence,
        }
        entries.append(entry)
        if page_class.startswith("UNKNOWN"):
            unknown.append(entry)

    print(f"pages: {len(entries)}")
    print("\n--- page classes ---")
    for name, count in classes.most_common():
        print(f"{count:5}  {name}")
    print("\n--- expected routes ---")
    for name, count in routes.most_common():
        print(f"{count:5}  {name}")

    if unknown:
        print(f"\n!!! {len(unknown)} unclassified pages - ground truth is incomplete")
        for entry in unknown[:20]:
            print(f"    {entry['doc']} p{entry['pageNumber']} {entry['evidence']}")

    payload = {
        "generator": "tools/corpus/label_corpus.py",
        "pageCount": len(entries),
        "classCounts": dict(classes.most_common()),
        "routeCounts": dict(routes.most_common()),
        "pages": entries,
    }
    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=1)
    print(f"\nwrote {args.out}")

    return 1 if unknown else 0


if __name__ == "__main__":
    raise SystemExit(main())
