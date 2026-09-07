"""Page measurement for the shared routing engine.

Kept apart from `app.py` so it can be imported without FastAPI - by the parity check in
`tools/corpus/check_vision_features.py`, which runs these functions against the corpus PDFs
and compares them with the recorded features the rules were calibrated on.

Every constant here is the one `tools/corpus/extract_features.py` used to build that corpus,
and they are load-bearing rather than tuning: the rules were written against ink boxes measured
at exactly this resolution and this ink level. Measuring differently would route differently
from the Windows agent while both sides ran identical rules, which is the single failure this
module exists to prevent.
"""

from __future__ import annotations

from typing import Any, Optional

try:  # optional: PDF rasterization
    import pypdfium2 as pdfium  # type: ignore  # noqa: F401  (imported for callers)
except ImportError:  # pragma: no cover
    pdfium = None

try:
    import numpy as np  # type: ignore
except ImportError:  # pragma: no cover
    np = None

try:
    import zxingcpp  # type: ignore
except ImportError:  # pragma: no cover
    zxingcpp = None

# Ink-bounding-box detection. 100 dpi locates a label region to about 0.25 mm and keeps a
#1266-page corpus fast; the measurements in docs/WINDOWS_CLIENT_PLAN.md section 1.2 come from
# this same setting, so they stay comparable.
BBOX_DPI = 100.0
INK_LEVEL = 200
NOISE_FRACTION = 0.002

# Barcode decoding. 200 dpi resolves Code 128 and PDF417 on a 4x6 label; pages where nothing is
# found are retried at 300 dpi, because MaxiCode and dense PDF417 on an A4-embedded label can
# fall below the decoder's module-size floor.
BARCODE_DPI_PRIMARY = 200.0
BARCODE_DPI_RETRY = 300.0

# OCR. 250 dpi reads the DHL template chrome ("*WAYBILL DOC*", "Not to be attached to package")
# reliably while keeping a page near a second.
OCR_DPI = 250.0

POINTS_PER_MM = 72.0 / 25.4


def available() -> bool:
    """True when this build can measure a page at all."""
    return pdfium is not None and np is not None


def mm(points: float) -> float:
    return round(points / POINTS_PER_MM, 2)


def ink_box(page: Any) -> Optional[dict]:
    """Bounding box of non-white content in millimetres, origin top-left.

    The noise floor is why this is not simply a bitmap bounding box: one stray dark pixel - a
    scan speck, a hairline rule bleeding off the sheet - would otherwise stretch the box to the
    page edge and turn a 4x6 label into an A4 document.
    """
    if np is None:
        return None

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

    scale = 25.4 / BBOX_DPI
    x0 = float(cols[0]) * scale
    x1 = float(cols[-1] + 1) * scale
    y0 = float(rows[0]) * scale
    y1 = float(rows[-1] + 1) * scale
    width = x1 - x0
    height = y1 - y0

    return {
        "x_mm": round(x0, 2),
        "y_mm": round(y0, 2),
        "width_mm": round(width, 2),
        "height_mm": round(height, 2),
        "aspect": round(height / width, 3) if width else None,
        "coverage": round(float(mask.mean()), 4),
    }


def barcodes(page: Any) -> list[dict]:
    """Every barcode on the page, with its position in page millimetres."""
    if zxingcpp is None:
        return []

    for dpi in (BARCODE_DPI_PRIMARY, BARCODE_DPI_RETRY):
        found: list[dict] = []
        image = page.render(scale=dpi / 72.0, grayscale=True).to_pil()
        scale = 25.4 / dpi

        for result in zxingcpp.read_barcodes(image, try_rotate=True, try_downscale=True):
            position = result.position
            xs = [position.top_left.x, position.top_right.x,
                  position.bottom_left.x, position.bottom_right.x]
            ys = [position.top_left.y, position.top_right.y,
                  position.bottom_left.y, position.bottom_right.y]
            found.append({
                # `.name` is the canonical zxing-cpp enum name (`Code128`, `DataMatrix`), which
                # is what rules are written against; `str()` gives the display form with spaces.
                "symbology": result.format.name,
                "value": result.text or "",
                "x_mm": round(min(xs) * scale, 2),
                "y_mm": round(min(ys) * scale, 2),
                "width_mm": round((max(xs) - min(xs)) * scale, 2),
                "height_mm": round((max(ys) - min(ys)) * scale, 2),
            })

        if found:
            return found

    return []


def recognise(page: Any, rects: list[dict], engine: Any) -> list[dict]:
    """OCR the rectangles a rule asked for, returning per-line boxes in page millimetres."""
    if engine is None or np is None or not rects:
        return []

    image = page.render(scale=OCR_DPI / 72.0).to_pil()
    px = OCR_DPI / 25.4
    regions: list[dict] = []

    for rect in rects:
        left = max(0, int(rect["x_mm"] * px))
        top = max(0, int(rect["y_mm"] * px))
        right = min(image.width, int((rect["x_mm"] + rect["width_mm"]) * px))
        bottom = min(image.height, int((rect["y_mm"] + rect["height_mm"]) * px))
        if right <= left or bottom <= top:
            regions.append({"rect": rect, "text": "", "lines": []})
            continue

        crop = np.array(image.crop((left, top, right, bottom)))
        result = engine.ocr(crop, cls=True)

        lines: list[dict] = []
        for entry in (result or [None])[0] or []:
            if not entry or len(entry) < 2 or not entry[1]:
                continue
            quad = entry[0]
            xs = [point[0] for point in quad]
            ys = [point[1] for point in quad]
            lines.append({
                "text": str(entry[1][0]),
                "x_mm": round(rect["x_mm"] + min(xs) / px, 2),
                "y_mm": round(rect["y_mm"] + min(ys) / px, 2),
                "width_mm": round((max(xs) - min(xs)) / px, 2),
                "height_mm": round((max(ys) - min(ys)) / px, 2),
            })

        regions.append({
            "rect": rect,
            "text": "\n".join(line["text"] for line in lines),
            "lines": lines,
        })

    return regions
