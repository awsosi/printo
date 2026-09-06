#!/usr/bin/env python3
"""Compare documents as they were on disk with the same documents as the spooler delivered them.

The Windows print path is not a pipe. It re-lays a document out on the way through: it turns
pages to fit the stock, it homogenises a job's mixed page sizes onto one media size, it scales
to the printable area, and it renders visible content only. Every one of those invalidates a
routing rule calibrated against the file on disk, and none of them are visible from the file.

This reads the manifest written by `Capture-Corpus.ps1` and prints, per page, what changed.

Usage:
    python tools/corpus/compare_captures.py [tests/capture/session]

Requires: PyMuPDF (fitz).
"""

from __future__ import annotations

import io
import json
import os
import sys

import fitz

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

MM = 25.4 / 72.0


def pages(path: str) -> list[dict]:
    """Everything about a page that a geometry rule could depend on."""
    out = []
    with fitz.open(path) as document:
        for index, page in enumerate(document):
            rect = page.rect
            images = page.get_image_info()
            largest = max(images, key=lambda i: i["width"] * i["height"], default=None)
            box = None
            if largest:
                x0, y0, x1, y1 = largest["bbox"]
                box = {
                    "w": (x1 - x0) * MM,
                    "h": (y1 - y0) * MM,
                    "x": x0 * MM,
                    "y": y0 * MM,
                    "px": (largest["width"], largest["height"]),
                    # Resolution the image is actually placed at, which is what OCR and
                    # barcode decoding get to work with.
                    "dpi": largest["width"] / ((x1 - x0) / 72.0) if x1 > x0 else 0,
                }
            out.append(
                {
                    "n": index + 1,
                    "w": rect.width * MM,
                    "h": rect.height * MM,
                    "text": len(page.get_text().strip()),
                    "images": len(images),
                    "box": box,
                }
            )
    return out


def describe(page: dict) -> str:
    box = page["box"]
    shape = f'{page["w"]:6.1f}x{page["h"]:6.1f}mm'
    orientation = "landscape" if page["w"] > page["h"] else "portrait "
    if box:
        art = f' img {box["w"]:5.1f}x{box["h"]:5.1f}mm @({box["x"]:5.1f},{box["y"]:5.1f}) {box["dpi"]:4.0f}dpi'
    else:
        art = " img none" + " " * 30
    return f'{shape} {orientation} text{page["text"]:5d}{art}'


def verdicts(source: dict, captured: dict) -> list[str]:
    """The transformations that matter, named."""
    found = []

    rotated = (
        abs(source["w"] - captured["h"]) < 3 and abs(source["h"] - captured["w"]) < 3
    )
    if rotated:
        found.append("ROTATED 90")
    elif abs(source["w"] - captured["w"]) > 3 or abs(source["h"] - captured["h"]) > 3:
        # A page that is neither the same size nor a rotation of it was substituted onto
        # different stock - which is what happens to every non-A4 page in a mixed job.
        found.append(
            f'MEDIA CHANGED {source["w"]:.0f}x{source["h"]:.0f} -> '
            f'{captured["w"]:.0f}x{captured["h"]:.0f}'
        )

    if source["box"] and captured["box"]:
        # Compare the artwork's long edge either way round, so a rotation is not read as a
        # scale change.
        s_long = max(source["box"]["w"], source["box"]["h"])
        c_long = max(captured["box"]["w"], captured["box"]["h"])
        if s_long > 1:
            ratio = c_long / s_long
            if abs(ratio - 1.0) > 0.02:
                found.append(f"SCALED x{ratio:.3f}")
        s_dpi, c_dpi = source["box"]["dpi"], captured["box"]["dpi"]
        if s_dpi > 1 and c_dpi / s_dpi < 0.9:
            found.append(f"RESAMPLED {s_dpi:.0f} -> {c_dpi:.0f} dpi")

    if source["text"] > 20 and captured["text"] == 0:
        found.append("TEXT LOST")
    elif source["text"] > 20 and captured["text"] > 0:
        found.append(f'text kept ({captured["text"]}/{source["text"]})')

    return found


def main() -> int:
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join("tests", "capture", "session")
    manifest_path = os.path.join(root, "manifest.json")
    if not os.path.exists(manifest_path):
        print(f"no manifest at {manifest_path}; run Capture-Corpus.ps1 first")
        return 2

    with open(manifest_path, encoding="utf-8-sig") as handle:
        manifest = json.load(handle)
    if isinstance(manifest, dict):
        manifest = [manifest]

    tally: dict[str, int] = {}

    for entry in manifest:
        source_path, job = entry.get("source"), entry.get("job")
        print("=" * 108)
        print(os.path.basename(source_path or "?"))
        if not job:
            print("  NOT CAPTURED")
            continue

        source = pages(source_path)
        captured = pages(os.path.join(root, job))

        if len(source) != len(captured):
            print(f"  PAGE COUNT CHANGED: {len(source)} -> {len(captured)}")
            tally["PAGE COUNT CHANGED"] = tally.get("PAGE COUNT CHANGED", 0) + 1

        for index in range(max(len(source), len(captured))):
            s = source[index] if index < len(source) else None
            c = captured[index] if index < len(captured) else None
            if s is None:
                print(f"  p{index + 1} ADDED    {describe(c)}")
                continue
            if c is None:
                print(f"  p{index + 1} DROPPED  {describe(s)}")
                continue

            found = verdicts(s, c)
            for verdict in found:
                key = verdict.split(" x")[0].split(" (")[0]
                tally[key] = tally.get(key, 0) + 1

            print(f"  p{index + 1} src  {describe(s)}")
            print(f"     out  {describe(c)}   {'; '.join(found) if found else 'unchanged'}")

    print("=" * 108)
    print("transformations observed, by page:")
    for key, count in sorted(tally.items(), key=lambda kv: -kv[1]):
        print(f"  {count:4d}  {key}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
