"""Tell ally rows from enemy rows by the tint of the row's stat area.

Measured on Valorant client screenshots (stat area, text excluded):
  ally rows    teal,          hue ~180 deg, saturation high (~170/255)
  enemy rows   muted maroon,  hue ~270-320 deg, saturation ~70-90
  your row     grey-olive,    saturation ~25-35 (the screenshot owner, so ally)
"""

from __future__ import annotations

import cv2
import numpy as np

from app.models import Side


def classify_band(band_bgr: np.ndarray) -> tuple[Side | None, float]:
    """Classify a horizontal strip of one scoreboard row."""
    if band_bgr.size == 0:
        return None, 0.0
    hsv = cv2.cvtColor(band_bgr, cv2.COLOR_BGR2HSV).reshape(-1, 3)
    background = hsv[(hsv[:, 2] < 190) & (hsv[:, 2] > 25)]  # drop white text and black outlines
    if len(background) < 0.2 * len(hsv):
        return None, 0.0
    hue = float(np.median(background[:, 0])) * 2.0
    sat = float(np.median(background[:, 1]))
    if sat >= 90 and 140 <= hue <= 220:
        return "ally", round(min(1.0, sat / 150), 3)
    if sat >= 90 and 30 <= hue <= 75:
        return "ally", 0.8  # bright gold "you" row (older client / other themes)
    if sat < 50:
        return "ally", 0.7  # desaturated "you" row
    if hue >= 240 or hue <= 20:
        return "enemy", round(min(1.0, 0.5 + sat / 200), 3)
    return None, 0.0


def classify_row(image_bgr: np.ndarray, x0: float, x1: float, yc: float, pitch: float) -> tuple[Side | None, float]:
    height, width = image_bgr.shape[:2]
    top = int(max(0, yc - 0.3 * pitch))
    bottom = int(min(height, yc + 0.3 * pitch))
    return classify_band(image_bgr[top:bottom, int(max(0, x0)) : int(min(width, x1))])


def fill_last_unknown(sides: list[Side | None]) -> list[tuple[Side | None, float]]:
    """With 10 rows, one unknown row and a 5/4 split, the unknown row must be on the short side."""
    allies, enemies = sides.count("ally"), sides.count("enemy")
    if len(sides) != 10 or sides.count(None) != 1 or {allies, enemies} != {4, 5}:
        return [(s, -1.0) for s in sides]
    missing: Side = "ally" if allies == 4 else "enemy"
    return [((missing, 0.5) if s is None else (s, -1.0)) for s in sides]
