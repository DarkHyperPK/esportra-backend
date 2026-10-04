"""Second pass over player names: re-read each unread or uncertain name from a tight, enlarged crop of
its cell, with the default recogniser and every installed script recogniser (Arabic, Korean), and keep
the most confident read.

The full-screen detector often finds no box at all on Arabic or Hangul names, so the crop is taken from
the layout (the name column) rather than from detected text.
"""

from __future__ import annotations

from statistics import median

import cv2
import numpy as np

from app.engine import OcrEngine
from app.valorant.columns import Column
from app.valorant.rows import Row, stat_boundary

REREAD_BELOW = 0.8
ACCEPT_FROM = 0.5
_TARGET_HEIGHT = 48  # the recognisers read ~48 px text best


def _name_box(row: Row, column_x0: float, right: float, pitch: float, size: tuple[int, int]) -> tuple[int, int, int, int]:
    height, width = size
    x0 = int(max(0, column_x0 - 0.15 * pitch))
    x1 = int(min(width, right))
    if row.name:
        # Keep the line we read (the old layout prints the agent name underneath).
        name_tokens = [t for t in row.tokens if t.x1 <= right + 1 and t.x0 >= x0 - 1]
        top = min((t.y0 for t in name_tokens), default=row.yc - 0.3 * pitch)
        bottom = max((t.y1 for t in name_tokens if t.yc <= row.yc + 0.1 * pitch), default=row.yc + 0.3 * pitch)
        y0, y1 = int(max(0, top - 0.08 * pitch)), int(min(height, bottom + 0.08 * pitch))
    else:
        y0, y1 = int(max(0, row.yc - 0.3 * pitch)), int(min(height, row.yc + 0.3 * pitch))
    return x0, y0, x1, y1


def _read(engine: OcrEngine, crop: np.ndarray, script: str | None) -> tuple[str, float]:
    if crop.size == 0:
        return "", 0.0
    scale = _TARGET_HEIGHT / crop.shape[0]
    if scale > 1.05:
        crop = cv2.resize(crop, None, fx=scale, fy=scale, interpolation=cv2.INTER_CUBIC)
    text, conf = engine.recognize(crop, script) if script else engine.recognize(crop)
    return text.strip(), conf


def best_read(engine: OcrEngine, crop: np.ndarray) -> tuple[str, float, str | None]:
    """Most confident read across the default and the installed script recognisers."""
    best = ("", 0.0, None)
    for script in (None, *getattr(engine, "scripts", ())):
        text, conf = _read(engine, crop, script)
        if any(ch.isalnum() for ch in text) and conf > best[1]:
            best = (text, conf, script)
    return best


def recover_names(image: np.ndarray, engine: OcrEngine, rows: list[Row], columns: list[Column], pitch: float) -> None:
    starts = [r.name_x0 for r in rows if r.name]
    if not starts or pitch <= 0:
        return
    column_x0 = median(starts)
    right = min(stat_boundary(columns), columns[0].x0)
    for row in rows:
        if row.name and row.name_conf >= REREAD_BELOW:
            continue
        x0, y0, x1, y1 = _name_box(row, column_x0, right, pitch, image.shape[:2])
        if x1 - x0 < 8 or y1 - y0 < 6:
            continue
        text, conf, script = best_read(engine, image[y0:y1, x0:x1])
        if conf >= ACCEPT_FROM and conf > row.name_conf:
            row.name = text.rsplit("|", 1)[-1].strip() if script is None else text
            row.name_conf = conf
            row.name_script = script
