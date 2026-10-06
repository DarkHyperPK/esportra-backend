"""Azure Computer Vision Read API engine — implements the OcrEngine Protocol.

Uses Azure's full-image text detection for `read()` (where it outperforms PaddleOCR on
Arabic/RTL and low-contrast screenshots), then delegates `recognize()` back to the bundled
RapidOcrEngine so the `recover_names` crop-level pass stays local and fast.
"""

from __future__ import annotations

import logging

import cv2
import httpx
import numpy as np

from app.engine import OcrToken, RapidOcrEngine

log = logging.getLogger(__name__)

_API_VERSION = "2024-02-01"
_FEATURES = "read"
_JPEG_QUALITY = [cv2.IMWRITE_JPEG_QUALITY, 92]


class AzureOcrEngine:
    """OcrEngine backed by Azure Computer Vision 4.0 Read API for full-image detection."""

    version = "azure-vision-read-v4"

    def __init__(self, endpoint: str, key: str, fallback: RapidOcrEngine) -> None:
        self._endpoint = endpoint.rstrip("/")
        self._key = key
        self._fallback = fallback
        self._url = (
            f"{self._endpoint}/computervision/imageanalysis:analyze"
            f"?api-version={_API_VERSION}&features={_FEATURES}"
        )

    @property
    def scripts(self) -> tuple[str, ...]:
        return self._fallback.scripts

    def read(self, image_bgr: np.ndarray) -> list[OcrToken]:
        ok, buf = cv2.imencode(".jpg", image_bgr, _JPEG_QUALITY)
        if not ok:
            log.warning("Azure OCR: failed to encode image as JPEG")
            return []
        try:
            response = httpx.post(
                self._url,
                headers={
                    "Ocp-Apim-Subscription-Key": self._key,
                    "Content-Type": "application/octet-stream",
                },
                content=bytes(buf),
                timeout=20,
            )
            response.raise_for_status()
        except httpx.HTTPStatusError as exc:
            log.warning("Azure OCR: HTTP %s — %s", exc.response.status_code, exc.response.text[:200])
            raise
        except httpx.RequestError as exc:
            log.warning("Azure OCR: request failed — %s", exc)
            raise

        return _parse_response(response.json())

    def recognize(self, crop_bgr: np.ndarray, script: str | None = None) -> tuple[str, float]:
        return self._fallback.recognize(crop_bgr, script)


def _correct_font(text: str) -> str:
    # Valorant's Tungsten font: angular 'v' is misread as '/' by Azure.
    # Riot IDs cannot contain '/', so every '/' is a misread letter.
    return text.replace("/", "v")


def _is_rtl_line(words: list[dict]) -> bool:
    """True when Azure returned words in right-to-left visual order.

    For RTL scripts (Arabic etc.) the first word in the array is the
    rightmost on screen, so its minimum x exceeds the last word's minimum x.
    """
    if len(words) < 2:
        return False
    def min_x(w: dict) -> float:
        pts = w.get("boundingPolygon") or []
        return min((p["x"] for p in pts), default=0.0)
    return min_x(words[0]) > min_x(words[-1])


def _parse_response(data: dict) -> list[OcrToken]:
    """Hybrid token strategy: per-word for LTR, per-line for RTL.

    LTR (English, Latin): per-word tokens preserve the precise x-positions
    the pipeline uses for column detection. Emitting one big line token would
    merge name + kills + deaths into one blob the pipeline cannot split.

    RTL (Arabic etc.): Azure's word array is in reading order (rightmost word
    first), but the pipeline re-sorts tokens by x left-to-right, reversing the
    name. Emit one line-level token so the pipeline sees a single correctly
    ordered string with the name's bounding box.
    """
    tokens: list[OcrToken] = []
    read_result = data.get("readResult") or {}
    for block in read_result.get("blocks") or []:
        for line in block.get("lines") or []:
            words: list[dict] = line.get("words") or []
            if not words:
                continue

            if _is_rtl_line(words):
                text = _correct_font((line.get("text") or "").strip())
                polygon: list[dict] = line.get("boundingPolygon") or []
                if not text or not polygon:
                    continue
                conf = min((float(w.get("confidence", 0.0)) for w in words), default=0.0)
                xs = [p["x"] for p in polygon]
                ys = [p["y"] for p in polygon]
                tokens.append(OcrToken(
                    text=text, conf=conf,
                    x0=float(min(xs)), y0=float(min(ys)),
                    x1=float(max(xs)), y1=float(max(ys)),
                ))
            else:
                for word in words:
                    text = _correct_font(word.get("text", "").strip())
                    conf = float(word.get("confidence", 0.0))
                    polygon = word.get("boundingPolygon") or []
                    if not text or not polygon:
                        continue
                    xs = [p["x"] for p in polygon]
                    ys = [p["y"] for p in polygon]
                    tokens.append(OcrToken(
                        text=text, conf=conf,
                        x0=float(min(xs)), y0=float(min(ys)),
                        x1=float(max(xs)), y1=float(max(ys)),
                    ))
    return tokens
