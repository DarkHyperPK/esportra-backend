"""Geometry of the scoreboard table that the OCR text alone doesn't give: where the table starts and
where each row's agent portrait sits.

The portrait is the first thing in a row, at the table's left edge. Between it and the name the
competitive layout shows a rank badge; custom games don't. The header bar spans the full table width,
so its left end is the table's left edge.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np


# name_x0 - table_left, in row pitches, on unstretched captures (used to measure stretch).
_GAP_WITH_RANK = 2.4
_GAP_WITHOUT_RANK = 1.1


@dataclass(frozen=True)
class PortraitBox:
    x0: int
    y0: int
    x1: int
    y1: int
    # Horizontal stretch of the capture (1.0 = native). >1 when a 4:3 frame was stretched wider.
    stretch: float


def _shared_edges(image_bgr: np.ndarray, bands: list[tuple[float, float]], limit: int) -> np.ndarray | None:
    """Per-x strength of vertical colour steps that every row shares (median across rows)."""
    height = image_bgr.shape[0]
    profiles = []
    for y0, y1 in bands:
        top, bottom = int(max(0, y0)), int(min(height, y1))
        if bottom - top >= 3:
            band = image_bgr[top:bottom, : limit + 1].astype(np.float32)
            profiles.append(np.abs(np.diff(band, axis=1)).sum(axis=2).mean(axis=0))
    return np.median(np.stack(profiles), axis=0) if len(profiles) >= 3 else None


def table_left(image_bgr: np.ndarray, bands: list[tuple[float, float]], name_x0: float, pitch: float) -> float | None:
    """The table's left edge: the first vertical colour step, from the left, that every row shares.

    Left of the table the page background is smooth and menu text differs per row, so the median
    edge strength there is ~0. Upscaled captures turn the edge into a soft ramp, so the first x that
    rises above a small floor is used rather than the strongest edge (which is often the portrait's
    own right border).
    """
    limit = int(min(image_bgr.shape[1] - 1, name_x0 - 0.5 * pitch))
    if limit < 8 or pitch <= 0:
        return None
    shared = _shared_edges(image_bgr, bands, limit)
    if shared is None or shared.max() < 18:
        return None
    rising = np.flatnonzero(shared >= max(10.0, 0.08 * float(shared.max())))
    return float(rising[0] + 1)


def portrait_box(image_bgr: np.ndarray, left: float | None, name_x0: float, yc: float, pitch: float) -> PortraitBox | None:
    """The agent portrait of one row, or None when the table edge is unknown."""
    if left is None or pitch <= 0 or name_x0 <= left:
        return None
    ratio = (name_x0 - left) / pitch
    stretch = ratio / (_GAP_WITH_RANK if ratio > 1.8 else _GAP_WITHOUT_RANK)
    stretch = float(np.clip(stretch, 0.6, 1.8))
    height, width = image_bgr.shape[:2]
    x0 = int(max(0, left))
    x1 = int(min(width, left + pitch * stretch))
    y0, y1 = int(max(0, yc - 0.5 * pitch)), int(min(height, yc + 0.5 * pitch))
    if x1 - x0 < 6 or y1 - y0 < 6:
        return None
    return PortraitBox(x0, y0, x1, y1, round(stretch, 3))


# Median width/height of single-digit stat boxes on native captures (measured 0.80-0.83 on every
# client layout and resolution in the fixtures), and how much it moves per unit of horizontal stretch.
_DIGIT_ASPECT = 0.81
_DIGIT_ASPECT_PER_STRETCH = 0.336
# Combined digit + portrait estimate: native captures 0.90-1.04, stretched 1.24-1.38, squeezed 0.64-0.78.
STRETCH_TOLERANCE = 0.15


def estimate_stretch(tokens: list, header_bottom: float) -> float:
    """Horizontal stretch of the capture from the shape of single-digit stat cells (1.0 = native).
    A 4:3 game stretched to 16:9 by the monitor and then photographed or captured reads ~1.3."""
    aspects = [t.w / t.h for t in tokens if t.y0 > header_bottom and t.h > 0 and len(t.text.strip()) == 1 and t.text.strip().isdigit()]
    if len(aspects) < 8:
        return 1.0
    return float(np.clip(1 + (float(np.median(aspects)) - _DIGIT_ASPECT) / _DIGIT_ASPECT_PER_STRETCH, 0.5, 2.0))


def unstretch(image_bgr: np.ndarray, stretch: float) -> np.ndarray:
    import cv2

    height, width = image_bgr.shape[:2]
    return cv2.resize(image_bgr, (max(1, round(width / stretch)), height), interpolation=cv2.INTER_AREA if stretch > 1 else cv2.INTER_CUBIC)


# Portrait width / row pitch on native captures (measured 0.78-0.90 across layouts).
_PORTRAIT_RATIO = 0.85


def portrait_stretch(image_bgr: np.ndarray, bands: list[tuple[float, float]], name_x0: float, pitch: float) -> float | None:
    """Horizontal stretch from the agent portraits, which are square on native captures: the strongest
    shared edge right of the table edge is the portrait's right border."""
    left = table_left(image_bgr, bands, name_x0, pitch)
    if left is None or pitch <= 0:
        return None
    shared = _shared_edges(image_bgr, bands, int(min(image_bgr.shape[1] - 1, name_x0 - 0.2 * pitch)))
    if shared is None:
        return None
    lo, hi = int(left + 0.5 * pitch), int(min(len(shared), left + 1.6 * pitch))
    if hi - lo < 3:
        return None
    right = lo + int(np.argmax(shared[lo:hi]))
    return float(np.clip((right - left) / pitch / _PORTRAIT_RATIO, 0.5, 2.0))
