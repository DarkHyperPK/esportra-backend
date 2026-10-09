"""Second pass over player names: re-read each unread or uncertain name from a tight, enlarged crop of
its cell, with the default recogniser and every installed script recogniser (Arabic, Korean), and keep
the most confident read.

The full-screen detector often finds no box at all on Arabic or Hangul names, so the crop is taken from
the layout (the name column) rather than from detected text.
"""

from __future__ import annotations

import logging
from statistics import median

import cv2
import numpy as np

from app.engine import OcrEngine
from app.valorant.columns import Column
from app.valorant.rows import Row, stat_boundary

log = logging.getLogger(__name__)

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
    """Most confident read across the default and the installed script recognisers.

    When two reads cover complementary scripts (e.g. Arabic chars + Latin digits),
    both are combined rather than one being discarded.
    """
    reads: list[tuple[str, float, str | None]] = []
    for script in (None, *getattr(engine, "scripts", ())):
        try:
            text, conf = _read(engine, crop, script)
        except Exception as exc:  # noqa: BLE001 - an optional recogniser failing must not fail the parse
            log.warning("OCR %s recogniser failed on a name: %s", script or "default", exc)
            continue
        if any(ch.isalnum() for ch in text):
            reads.append((text, conf, script))

    if not reads:
        return ("", 0.0, None)

    # Combine complementary reads when one recogniser returns only script chars and
    # another returns only ASCII chars (e.g. Arabic "عبس" + default "404" → "عبس404").
    non_ascii = [(t, c, s) for t, c, s in reads if any(ord(ch) > 127 for ch in t) and c >= ACCEPT_FROM]
    ascii_only = [(t, c, s) for t, c, s in reads if all(ord(ch) <= 127 for ch in t) and c >= ACCEPT_FROM]
    if non_ascii and ascii_only:
        best_na = max(non_ascii, key=lambda r: r[1])
        best_a = max(ascii_only, key=lambda r: r[1])
        na_text = best_na[0]
        # Script read already captured both scripts (e.g. Arabic recogniser returned عبس404) — prefer it.
        if any(ch.isalnum() and ord(ch) <= 127 for ch in na_text):
            return best_na
        # Script read is purely non-ASCII alphanumeric — combine with the ASCII-only portion.
        if all(ord(ch) > 127 or not ch.isalnum() for ch in na_text):
            return (na_text + best_a[0], min(best_na[1], best_a[1]), best_na[2])

    return max(reads, key=lambda r: r[1])


def _plausible_name(name: str) -> bool:
    """True when the name looks like an actual player name rather than a badge glyph or single character."""
    return sum(1 for ch in name if ch.isalnum()) > 1


def recover_names(image: np.ndarray, engine: OcrEngine, rows: list[Row], columns: list[Column], pitch: float) -> None:
    if pitch <= 0:
        return
    starts = [r.name_x0 for r in rows if r.name]
    # When no names were detected by the initial pass (e.g. all non-Latin script scoreboard), crop
    # from the left image edge. Rank-badge glyphs (x ~ 50 px) found by the initial pass also give a
    # reasonable anchor — the full name area starts just right of them.
    column_x0 = median(starts) if starts else 0.0
    right = min(stat_boundary(columns), columns[0].x0)
    for row in rows:
        # Skip only rows with a confident, plausible name. Single-char badge glyphs ("口", "4") must
        # be retried so the Arabic/Korean recogniser can overwrite them.
        if _plausible_name(row.name) and row.name_conf >= REREAD_BELOW:
            continue
        x0, y0, x1, y1 = _name_box(row, column_x0, right, pitch, image.shape[:2])
        if x1 - x0 < 8 or y1 - y0 < 6:
            continue
        text, conf, script = best_read(engine, image[y0:y1, x0:x1])
        if conf >= ACCEPT_FROM and conf > row.name_conf:
            row.name = text.rsplit("|", 1)[-1].strip() if script is None else text
            row.name_conf = conf
            row.name_script = script
