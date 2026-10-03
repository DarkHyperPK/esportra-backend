"""OCR engine abstraction. Production uses RapidOCR (PaddleOCR PP-OCR models on ONNX Runtime)."""

from __future__ import annotations

import threading
from dataclasses import dataclass
from typing import Protocol

import numpy as np


@dataclass(frozen=True)
class OcrToken:
    text: str
    conf: float
    x0: float
    y0: float
    x1: float
    y1: float

    @property
    def xc(self) -> float:
        return (self.x0 + self.x1) / 2

    @property
    def yc(self) -> float:
        return (self.y0 + self.y1) / 2

    @property
    def h(self) -> float:
        return self.y1 - self.y0

    @property
    def w(self) -> float:
        return self.x1 - self.x0


class OcrEngine(Protocol):
    version: str

    def read(self, image_bgr: np.ndarray) -> list[OcrToken]: ...

    def recognize(self, crop_bgr: np.ndarray) -> tuple[str, float]: ...


class RapidOcrEngine:
    """Thin wrapper; the model is loaded once per process.

    RapidOCR keeps per-call flags (use_det/use_cls) as mutable state on the instance, so every
    call passes all flags explicitly and calls are serialized with a lock.
    """

    def __init__(self) -> None:
        from rapidocr import RapidOCR  # imported lazily so unit tests don't load ONNX models

        self._lock = threading.Lock()
        self._ocr = RapidOCR(
            params={
                "Global.use_cls": False,
                "Global.text_score": 0.3,
                "Global.log_level": "error",
                "Global.max_side_len": 2560,
            }
        )
        from importlib.metadata import version

        self.version = f"rapidocr-{version('rapidocr')}/pp-ocr"

    def read(self, image_bgr: np.ndarray) -> list[OcrToken]:
        with self._lock:
            result = self._ocr(image_bgr, use_det=True, use_cls=False, use_rec=True)
        boxes, txts = getattr(result, "boxes", None), getattr(result, "txts", None)
        if boxes is None or txts is None:
            return []
        tokens: list[OcrToken] = []
        for box, text, score in zip(boxes, txts, result.scores):
            xs = [float(p[0]) for p in box]
            ys = [float(p[1]) for p in box]
            cleaned = str(text).strip()
            if cleaned:
                tokens.append(OcrToken(cleaned, float(score), min(xs), min(ys), max(xs), max(ys)))
        return tokens

    def recognize(self, crop_bgr: np.ndarray) -> tuple[str, float]:
        """Recognition only (no detection) for a crop we already know holds one text line."""
        if crop_bgr.size == 0:
            return "", 0.0
        with self._lock:
            result = self._ocr(crop_bgr, use_det=False, use_cls=False, use_rec=True)
        if not result.txts:
            return "", 0.0
        return str(result.txts[0]).strip(), float(result.scores[0])
