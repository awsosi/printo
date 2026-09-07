FROM python:3.12-slim AS base

WORKDIR /app

# BUILD_PROFILE=minimal  → text heuristics only, no rasterizer: /v1/page-features returns 503
#                          and the worker falls back to its own classifier.
# BUILD_PROFILE=geometry → rasterizer, ink box and barcodes. What the shared routing engine
#                          needs, at about a tenth of the image size of `full`.
# BUILD_PROFILE=full     → geometry plus PaddleOCR, for rules that need to read a page.
ARG BUILD_PROFILE=geometry
COPY requirements.txt requirements-geometry.txt requirements-full.txt ./
RUN if [ "$BUILD_PROFILE" = "full" ]; then \
      pip install --no-cache-dir -r requirements-full.txt; \
    elif [ "$BUILD_PROFILE" = "geometry" ]; then \
      pip install --no-cache-dir -r requirements-geometry.txt; \
    else \
      pip install --no-cache-dir -r requirements.txt; \
    fi

COPY app.py features.py ./

EXPOSE 6000
HEALTHCHECK --interval=15s --timeout=3s CMD python -c "import urllib.request;urllib.request.urlopen('http://localhost:6000/health')"

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "6000"]
