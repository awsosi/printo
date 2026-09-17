"""Reproduce the Windows print path on the corpus PDFs, for routing the corpus as printed.

What printing through the Printo virtual printer does to a page was measured on seven real
captures (plan sections 5.0 and 5.0a, `tests/capture/`): the text layer is removed, the page is
delivered on A4 portrait whatever its own medium, a landscape page is turned so that a point
(x, y) lands at (y, W - x), and content is placed 1:1 at the top-left, never scaled. This script
does exactly that to every page, as a 300 dpi greyscale raster.

The copies route rule-for-rule like the seven captures (21 of 21 pages), which is what makes
them a fair stand-in for the 258 documents nobody has printed. Their features are recorded in
`tests/corpus/printed-features.jsonl.gz` by `PrintedCorpusExport` in the agent test suite.

    python tools/corpus/simulate_print.py <printo-materials> <output-dir>
"""

import io
import os
import sys
from concurrent.futures import ProcessPoolExecutor
from glob import glob

import fitz  # PyMuPDF
from PIL import Image

A4_POINTS = (595.2756, 841.8898)
DPI = 300


def simulate(job: tuple[str, str]) -> str:
    source, target = job
    if os.path.exists(target):
        return target

    printed = fitz.open()
    for page in fitz.open(source):
        pixmap = page.get_pixmap(dpi=DPI, colorspace=fitz.csGRAY)
        image = Image.frombytes("L", (pixmap.width, pixmap.height), pixmap.samples)
        width, height = page.rect.width, page.rect.height
        if width > height:
            # PIL turns counter-clockwise, which maps (x, y) to (y, W - x) as measured.
            image = image.transpose(Image.Transpose.ROTATE_90)
            width, height = height, width

        buffer = io.BytesIO()
        image.save(buffer, format="PNG")
        sheet = printed.new_page(width=A4_POINTS[0], height=A4_POINTS[1])
        sheet.insert_image(fitz.Rect(0, 0, width, height), stream=buffer.getvalue())

    os.makedirs(os.path.dirname(target), exist_ok=True)
    printed.save(target, deflate=True)
    return target


def main() -> None:
    corpus, output = sys.argv[1], sys.argv[2]
    jobs = [
        (source, os.path.join(output, os.path.relpath(source, corpus)))
        for source in sorted(glob(os.path.join(corpus, "*", "*.pdf")))
    ]
    with ProcessPoolExecutor() as pool:
        for done, _ in enumerate(pool.map(simulate, jobs), 1):
            if done % 25 == 0:
                print(f"{done}/{len(jobs)}", flush=True)
    print(f"simulated {len(jobs)} documents into {output}")


if __name__ == "__main__":
    main()
