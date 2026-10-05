# Esportra scoreboard OCR — internal service, not exposed publicly.
FROM python:3.11-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    SCOREBOARD_OCR_ASSET_DIR=/data/valorant-assets

WORKDIR /srv
COPY requirements.txt requirements-ocr.txt ./
RUN pip install -r requirements.txt \
    && pip install --no-deps -r requirements-ocr.txt

COPY app ./app
# Arabic and Korean name recognisers (PaddleOCR PP-OCRv5, checksum-verified). Never fails the build:
# the service retries a failed download at start-up, and /health lists installed and missing scripts.
RUN python -m app.download_models

# The models directory stays writable so the service can retry a failed download at start-up.
RUN useradd --system --uid 10001 --home /srv ocr \
    && mkdir -p /data/valorant-assets \
    && chown -R ocr /data \
    && chown -R ocr "$(python -c 'import pathlib, rapidocr; print(pathlib.Path(rapidocr.__file__).parent / "models")')"
USER ocr

EXPOSE 8095
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8095/health', timeout=3)"

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8095", "--workers", "1", "--no-server-header"]
