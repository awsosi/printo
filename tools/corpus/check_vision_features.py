#!/usr/bin/env python3
"""Check that the Vision Service measures pages the way the corpus was measured.

The server path routes with the shared engine, and the shared engine's rules were calibrated
against ink boxes produced by `extract_features.py`. The service measures its own. If the two
ever disagree, the worker and the Windows agent will route the same document differently while
running identical rules - the hardest kind of defect to see, because every test that looks at
one side alone passes.

So this runs the *production* measuring code (`services/vision/features.py`) over the corpus
PDFs and compares it, page for page, with the recorded features the golden corpus is built on.

Usage:
    python tools/corpus/check_vision_features.py <corpus-dir> [--sample 60] [--all]

Requires: pypdfium2, numpy (and zxing-cpp for --barcodes). Exits non-zero on any disagreement.
"""

from __future__ import annotations

import argparse
import gzip
import json
import os
import random
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(REPO_ROOT, "services", "vision"))

import features  # noqa: E402  (path set above, deliberately)
import pypdfium2 as pdfium  # noqa: E402

FEATURES_PATH = os.path.join(REPO_ROOT, "tests", "corpus", "features.jsonl.gz")

# The recorded corpus stores millimetres rounded to two places, and so does the service; the
# only difference either side can legitimately produce is that rounding.
TOLERANCE_MM = 0.01


def load_recorded() -> dict[str, dict]:
    recorded: dict[str, dict] = {}
    with gzip.open(FEATURES_PATH, "rt", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            record = json.loads(line)
            recorded[f"{record['doc']}#{record['pageNumber']}"] = record
    return recorded


def compare(recorded: dict, measured: dict | None, page_key: str, want_barcodes: bool) -> list[str]:
    problems: list[str] = []
    expected = recorded.get("inkBox")

    if (expected is None) != (measured is None):
        return [f"{page_key}: ink box {'expected' if expected else 'not expected'} but {'measured' if measured else 'not measured'}"]

    if expected is None or measured is None:
        return problems

    for recorded_field, measured_field in (
        ("xMm", "x_mm"), ("yMm", "y_mm"), ("widthMm", "width_mm"), ("heightMm", "height_mm")
    ):
        delta = abs(float(expected[recorded_field]) - float(measured[measured_field]))
        if delta > TOLERANCE_MM:
            problems.append(
                f"{page_key}: {recorded_field} {expected[recorded_field]} vs {measured[measured_field]} "
                f"(delta {delta:.3f} mm)")

    if expected.get("aspect") is not None and measured.get("aspect") is not None:
        if abs(float(expected["aspect"]) - float(measured["aspect"])) > 0.002:
            problems.append(f"{page_key}: aspect {expected['aspect']} vs {measured['aspect']}")

    if want_barcodes:
        expected_values = sorted(entry["value"] for entry in recorded.get("barcodes") or [])
        measured_values = sorted(entry["value"] for entry in measured.get("barcodes") or [])
        if expected_values != measured_values:
            problems.append(f"{page_key}: barcodes {expected_values} vs {measured_values}")

    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("corpus_dir")
    parser.add_argument("--sample", type=int, default=60, help="documents to check (default 60)")
    parser.add_argument("--all", action="store_true", help="check every document")
    parser.add_argument("--barcodes", action="store_true", help="also compare decoded barcodes (slow)")
    parser.add_argument("--seed", type=int, default=7)
    args = parser.parse_args()

    if not features.available():
        print("pypdfium2 and numpy are required", file=sys.stderr)
        return 2

    recorded = load_recorded()
    documents = sorted({key.rsplit("#", 1)[0] for key in recorded})

    if not args.all:
        random.seed(args.seed)
        documents = sorted(random.sample(documents, min(args.sample, len(documents))))

    problems: list[str] = []
    pages_checked = 0

    for relative in documents:
        path = os.path.join(args.corpus_dir, relative.replace("/", os.sep))
        if not os.path.exists(path):
            print(f"missing: {relative}", file=sys.stderr)
            continue

        pdf = pdfium.PdfDocument(path)
        try:
            for index in range(len(pdf)):
                page_key = f"{relative}#{index + 1}"
                if page_key not in recorded:
                    continue

                page = pdf[index]
                measured = features.ink_box(page)
                if measured is not None and args.barcodes:
                    measured = dict(measured, barcodes=features.barcodes(page))

                problems.extend(compare(recorded[page_key], measured, page_key, args.barcodes))
                pages_checked += 1
                page.close()
        finally:
            pdf.close()

    print(f"checked {pages_checked} pages across {len(documents)} documents")

    if problems:
        print(f"{len(problems)} disagreement(s):")
        for problem in problems[:20]:
            print(f"  {problem}")
        return 1

    print("the Vision Service measures the corpus exactly as the rules were calibrated on it")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
