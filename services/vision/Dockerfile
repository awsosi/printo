FROM python:3.12-slim AS base

WORKDIR /app

# BUILD_PROFILE=minimal → heuristics only; BUILD_PROFILE=full → rasterizer + barcodes + OCR.
ARG BUILD_PROFILE=minimal
COPY requirements.txt requirements-full.txt ./
RUN if [ "$BUILD_PROFILE" = "full" ]; then \
      pip install --no-cache-dir -r requirements-full.txt; \
    else \
      pip install --no-cache-dir -r requirements.txt; \
    fi

COPY app.py ./

EXPOSE 6000
HEALTHCHECK --interval=15s --timeout=3s CMD python -c "import urllib.request;urllib.request.urlopen('http://localhost:6000/health')"

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "6000"]
