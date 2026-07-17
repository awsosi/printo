"""Printo Vision Service.

Classifies rendered PDF pages / page images as shipping labels vs. documents.
Contract: docs/VISION_SERVICE.md in the repo root.

The service is layered so it degrades gracefully:
  1. Text-layer heuristics (always available, mirrors the worker's fallback).
  2. PDF rasterization via pypdfium2 (optional dependency).
  3. Barcode detection via zxing-cpp (optional dependency).
  4. OCR via PaddleOCR (optional dependency) for pages without a text layer.

Install the optional extras from requirements-full.txt to enable layers 2-4.
"""

from __future__ import annotations

import base64
import io
import re
from typing import Any, Optional

from fastapi import FastAPI
from pydantic import BaseModel

try:  # optional: PDF rasterization
    import pypdfium2 as pdfium  # type: ignore
except ImportError:  # pragma: no cover
    pdfium = None

try:  # optional: barcode detection
    import zxingcpp  # type: ignore
    from PIL import Image  # type: ignore
except ImportError:  # pragma: no cover
    zxingcpp = None
    Image = None

try:  # optional: OCR for scanned pages
    from paddleocr import PaddleOCR  # type: ignore

    _ocr_engine: Optional[Any] = None

    def get_ocr() -> Any:
        global _ocr_engine
        if _ocr_engine is None:
            _ocr_engine = PaddleOCR(use_angle_cls=True, lang="en", show_log=False)
        return _ocr_engine

except ImportError:  # pragma: no cover
    def get_ocr() -> Any:
        return None


app = FastAPI(title="printo-vision", version="0.1.0")

# ---------------------------------------------------------------------------
# Heuristics — keep in sync with apps/worker/src/classify/heuristic-classifier.ts
# ---------------------------------------------------------------------------

CARRIERS: list[tuple[str, list[re.Pattern[str]]]] = [
    ("DHL", [re.compile(p, re.I) for p in [r"\bdhl\b", r"express worldwide", r"\bpaket\b", r"deutsche post"]]),
    ("UPS", [re.compile(p, re.I) for p in [r"\bups\b", r"united parcel", r"\b1Z[0-9A-Z]{15,16}\b", r"ups (standard|express|ground|saver)"]]),
    ("FEDEX", [re.compile(p, re.I) for p in [r"fed\s?ex", r"smartpost", r"fedex (ground|express|international)"]]),
    ("DPD", [re.compile(p, re.I) for p in [r"\bdpd\b", r"dpd (classic|pickup)"]]),
    ("GLS", [re.compile(p, re.I) for p in [r"\bgls\b", r"general logistics systems"]]),
    ("INPOST", [re.compile(p, re.I) for p in [r"inpost", r"paczkomat"]]),
    ("POCZTA_POLSKA", [re.compile(p, re.I) for p in [r"poczta polska", r"pocztex"]]),
]

LABEL_KEYWORDS = [
    "ship to", "ship from", "shipper", "consignee", "tracking number", "tracking no",
    "tracking #", "waybill", "airway bill", "list przewozowy", "nadawca", "odbiorca",
    "przesyłka", "shipment date", "service level", "delivery address", "parcel",
    "package weight",
]

RETURN_KEYWORDS = [
    "return label", "return shipment", "returns label", "retour", "retoure",
    "rücksendung", "rucksendung", "zwrot", "etykieta zwrotna", "etykieta zwrotu",
    "przesyłka zwrotna",
]

DOCUMENT_KEYWORDS = [
    "invoice", "faktura", "vat", "iban", "payment terms", "termin płatności",
    "suma brutto", "total amount", "order confirmation", "packing slip", "specyfikacja",
]

TRACKING_PATTERNS = [
    (re.compile(r"\b1Z[0-9A-Z]{15,16}\b"), "tracking:ups-1z"),
    (re.compile(r"\bJJD[0-9]{16,20}\b", re.I), "tracking:dhl-jjd"),
    (re.compile(r"\b\d{2}\s?\d{4}\s?\d{4}\s?\d{10}\b"), "tracking:dhl-paket"),
    (re.compile(r"\b(96|00)\d{18,20}\b"), "tracking:gs1-sscc"),
]

# Barcode symbologies that strongly indicate a shipping label.
LOGISTICS_SYMBOLOGIES = {"Code128", "MaxiCode", "DataMatrix", "PDF417", "ITF", "Code39"}

LABEL_PAGE_MAX_WIDTH_PT = 340
LABEL_PAGE_MAX_HEIGHT_PT = 500
LABEL_CONFIDENCE_THRESHOLD = 0.5


class ClassifyRequest(BaseModel):
    page_number: int = 1
    text: Optional[str] = None
    page_width: Optional[float] = None
    page_height: Optional[float] = None
    page_pdf_base64: Optional[str] = None
    image_png_base64: Optional[str] = None


class BarcodeOut(BaseModel):
    symbology: str
    value: Optional[str] = None
    bounding_box: Optional[dict] = None


class ClassifyResponse(BaseModel):
    page_number: int
    page_class: str
    confidence: float
    carrier: Optional[str] = None
    is_return: bool = False
    barcodes: list[BarcodeOut] = []
    evidence: list[str] = []


def rasterize(request: ClassifyRequest) -> Optional["Image.Image"]:
    """Render the incoming page (PDF or PNG) to a PIL image, if backends allow."""
    if Image is None:
        return None
    if request.image_png_base64:
        return Image.open(io.BytesIO(base64.b64decode(request.image_png_base64))).convert("RGB")
    if request.page_pdf_base64 and pdfium is not None:
        pdf = pdfium.PdfDocument(base64.b64decode(request.page_pdf_base64))
        try:
            page = pdf[0]
            bitmap = page.render(scale=200 / 72)  # ~200 dpi
            return bitmap.to_pil().convert("RGB")
        finally:
            pdf.close()
    return None


def detect_barcodes(image: Optional["Image.Image"]) -> list[BarcodeOut]:
    if image is None or zxingcpp is None:
        return []
    results = []
    for barcode in zxingcpp.read_barcodes(image):
        position = barcode.position
        box = None
        if position is not None:
            xs = [position.top_left.x, position.top_right.x, position.bottom_left.x, position.bottom_right.x]
            ys = [position.top_left.y, position.top_right.y, position.bottom_left.y, position.bottom_right.y]
            box = {"x": min(xs), "y": min(ys), "width": max(xs) - min(xs), "height": max(ys) - min(ys)}
        results.append(BarcodeOut(symbology=str(barcode.format), value=barcode.text or None, bounding_box=box))
    return results


def run_ocr(image: Optional["Image.Image"]) -> str:
    if image is None:
        return ""
    engine = get_ocr()
    if engine is None:
        return ""
    import numpy as np  # paddleocr already depends on numpy

    result = engine.ocr(np.array(image), cls=True)
    lines: list[str] = []
    for page in result or []:
        for entry in page or []:
            if entry and len(entry) > 1 and entry[1]:
                lines.append(str(entry[1][0]))
    return "\n".join(lines)


def classify(request: ClassifyRequest) -> ClassifyResponse:
    text = (request.text or "").strip()
    image = None
    barcodes: list[BarcodeOut] = []
    evidence: list[str] = []

    needs_vision = not text or request.image_png_base64 or request.page_pdf_base64
    if needs_vision:
        image = rasterize(request)
        barcodes = detect_barcodes(image)
        if not text:
            ocr_text = run_ocr(image)
            if ocr_text:
                text = ocr_text
                evidence.append("ocr:paddle")

    normalized = re.sub(r"\s+", " ", text).lower()
    score = 0.0

    carrier = None
    for name, patterns in CARRIERS:
        if any(p.search(normalized) for p in patterns):
            carrier = name
            score += 0.35
            evidence.append(f"carrier:{name.lower()}")
            break

    keyword_hits = [k for k in LABEL_KEYWORDS if k in normalized]
    score += min(0.45, len(keyword_hits) * 0.15)
    evidence.extend(f"keyword:{k}" for k in keyword_hits)

    for pattern, name in TRACKING_PATTERNS:
        if pattern.search(text):
            score += 0.25
            evidence.append(name)
            break

    logistics_barcodes = [b for b in barcodes if b.symbology in LOGISTICS_SYMBOLOGIES]
    if logistics_barcodes:
        score += min(0.4, 0.2 * len(logistics_barcodes))
        evidence.append(f"barcodes:{len(logistics_barcodes)}")
        if any(b.symbology == "MaxiCode" for b in logistics_barcodes):
            carrier = carrier or "UPS"
            evidence.append("barcode:maxicode")

    if (
        request.page_width
        and request.page_height
        and request.page_width <= LABEL_PAGE_MAX_WIDTH_PT
        and request.page_height <= LABEL_PAGE_MAX_HEIGHT_PT
    ):
        score += 0.3
        evidence.append("layout:label-sized-page")

    document_hits = [k for k in DOCUMENT_KEYWORDS if k in normalized]
    score -= min(0.5, len(document_hits) * 0.25)
    evidence.extend(f"document:{k}" for k in document_hits)

    return_hit = next((k for k in RETURN_KEYWORDS if k in normalized), None)
    if return_hit:
        evidence.append(f"return:{return_hit}")

    confidence = max(0.0, min(1.0, score))
    is_label = confidence >= LABEL_CONFIDENCE_THRESHOLD
    if not is_label:
        page_class = "DOCUMENT_A4"
        confidence = max(0.0, min(1.0, 1 - confidence))
    elif return_hit:
        page_class = "RETURN_LABEL_A4"
    else:
        page_class = "OUTGOING_LABEL_THERMAL"

    return ClassifyResponse(
        page_number=request.page_number,
        page_class=page_class,
        confidence=confidence,
        carrier=carrier,
        is_return=bool(return_hit),
        barcodes=barcodes,
        evidence=evidence,
    )


@app.get("/health")
def health() -> dict:
    return {
        "service": "vision",
        "status": "ok",
        "backends": {
            "pdf_rasterizer": pdfium is not None,
            "barcodes": zxingcpp is not None,
            "ocr": get_ocr() is not None,
        },
    }


@app.post("/v1/classify-page", response_model=ClassifyResponse)
def classify_page(request: ClassifyRequest) -> ClassifyResponse:
    return classify(request)
