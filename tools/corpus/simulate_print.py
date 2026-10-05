"""Reproduce the Windows print path on the corpus PDFs, for routing the corpus as printed.

What printing through the Printo virtual printer does to a page was measured on seven real
captures (plan sections 5.0 and 5.0a, `tests/capture/`): the text layer is removed, the page is
delivered on A4 portrait whatever its own medium, a landscape page is turned so that a point
(x, y) lands at (y, W - x), and content is placed 1:1 at the top-left, never scaled. This script
does exactly that to every page, as a 300 dpi greyscale raster.

Two print-dialog settings change what arrives, and both can be reproduced here: the resolution
chosen on the Printo queue (`--dpi`, which the class driver resamples images to) and the scale
chosen in the application (`--scale 0.9`, or `--fit` to fill the sheet, centred - what Chrome's
"Fit to printable area" does on a queue with no margins). The defaults are the measured path.

The copies route rule-for-rule like the seven captures (21 of 21 pages), which is what makes
them a fair stand-in for the 258 documents nobody has printed. Their features are recorded in
`tests/corpus/printed-features.jsonl.gz` by `PrintedCorpusExport` in the agent test suite.

    python tools/corpus/simulate_print.py <printo-materials> <output-dir> [--dpi 203] [--scale 0.9 | --fit]
"""

import argparse
import io
import os
from concurrent.futures import ProcessPoolExecutor
from glob import glob

import fitz  # PyMuPDF
from PIL import Image

A4_POINTS = (595.2756, 841.8898)
DPI = 300


def simulate(job: tuple[str, str, int, float | None]) -> str:
    source, target, dpi, scale = job
    if os.path.exists(target):
        return target

    printed = fitz.open()
    for page in fitz.open(source):
        pixmap = page.get_pixmap(dpi=dpi, colorspace=fitz.csGRAY)
        image = Image.frombytes("L", (pixmap.width, pixmap.height), pixmap.samples)
        width, height = page.rect.width, page.rect.height
        if width > height:
            # PIL turns counter-clockwise, which maps (x, y) to (y, W - x) as measured.
            image = image.transpose(Image.Transpose.ROTATE_90)
            width, height = height, width

        buffer = io.BytesIO()
        image.save(buffer, format="PNG")
        sheet = printed.new_page(width=A4_POINTS[0], height=A4_POINTS[1])
        if scale is None:
            # Fit: as large as the sheet allows, centred.
            fit = min(A4_POINTS[0] / width, A4_POINTS[1] / height)
            left = (A4_POINTS[0] - width * fit) / 2
            top = (A4_POINTS[1] - height * fit) / 2
            placed = fitz.Rect(left, top, left + width * fit, top + height * fit)
        else:
            placed = fitz.Rect(0, 0, width * scale, height * scale)
        # Whatever the scale pushes past the sheet is lost, as it is on paper.
        sheet.insert_image(placed, stream=buffer.getvalue())

    os.makedirs(os.path.dirname(target), exist_ok=True)
    printed.save(target, deflate=True)
    return target


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("corpus")
    parser.add_argument("output")
    parser.add_argument("--dpi", type=int, default=DPI, help="the Printo queue's resolution")
    sizing = parser.add_mutually_exclusive_group()
    sizing.add_argument("--scale", type=float, default=1.0, help="the application's scale, 1.0 = 100%%")
    sizing.add_argument("--fit", action="store_true", help="fit each page to the sheet")
    args = parser.parse_args()

    scale = None if args.fit else args.scale
    jobs = [
        (source, os.path.join(args.output, os.path.relpath(source, args.corpus)), args.dpi, scale)
        for source in sorted(glob(os.path.join(args.corpus, "*", "*.pdf")))
    ]
    with ProcessPoolExecutor() as pool:
        for done, _ in enumerate(pool.map(simulate, jobs), 1):
            if done % 25 == 0:
                print(f"{done}/{len(jobs)}", flush=True)
    print(f"simulated {len(jobs)} documents into {args.output}")


if __name__ == "__main__":
    main()
