"""Tell ally rows from enemy rows by the tint of the row's stat area.

Measured on Valorant client screenshots (stat area, text excluded):
  ally rows    teal,          hue ~180 deg, saturation high (~170/255)
  enemy rows   muted maroon,  hue ~270-320 deg, saturation ~70-90
  your row     grey-olive,    saturation ~25-35, or blue-grey (hue ~216, saturation ~52) on the 2026 client
               (the screenshot owner, so ally)
  party rows   gold (an ally in the owner's party)
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
    if sat < 50 or (sat < 70 and 190 <= hue <= 240):
        return "ally", 0.7  # desaturated "you" row (grey-olive, or blue-grey on the 2026 client)
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


def row_tint(image_bgr: np.ndarray, x0: float, x1: float, yc: float, pitch: float) -> np.ndarray | None:
    """Median colour of a row's stat area as a point (hue on the circle x saturation, value)."""
    height, width = image_bgr.shape[:2]
    band = image_bgr[int(max(0, yc - 0.3 * pitch)) : int(min(height, yc + 0.3 * pitch)), int(max(0, x0)) : int(min(width, x1))]
    if band.size == 0:
        return None
    hsv = cv2.cvtColor(band, cv2.COLOR_BGR2HSV).reshape(-1, 3).astype(np.float32)
    hsv = hsv[(hsv[:, 2] < 190) & (hsv[:, 2] > 25)]
    if len(hsv) < 20:
        return None
    hue = np.radians(np.median(hsv[:, 0]) * 2.0)
    sat, val = float(np.median(hsv[:, 1])) / 255, float(np.median(hsv[:, 2])) / 255
    return np.array([sat * np.cos(hue), sat * np.sin(hue), val], dtype=np.float32)


def relative_sides(tints: list[np.ndarray | None], sides: list[tuple[Side | None, float]]) -> list[tuple[Side | None, float]]:
    """Rows the absolute colour rules couldn't place (colour-blind palettes, hover highlights, unusual
    gamma) go to whichever team's rows they look closest to. Needs at least two placed rows per team."""
    known = {side: [t for t, (s, _) in zip(tints, sides) if s == side and t is not None] for side in ("ally", "enemy")}
    if len(known["ally"]) < 2 or len(known["enemy"]) < 2:
        return sides
    centres = {side: np.mean(points, axis=0) for side, points in known.items()}
    out = []
    for tint, (side, conf) in zip(tints, sides):
        if side is None and tint is not None:
            d_ally, d_enemy = (float(np.linalg.norm(tint - centres[s])) for s in ("ally", "enemy"))
            closer: Side = "ally" if d_ally < d_enemy else "enemy"
            margin = abs(d_ally - d_enemy) / max(d_ally + d_enemy, 1e-6)
            out.append((closer, 0.5) if margin > 0.2 else (None, 0.0))
        else:
            out.append((side, conf))
    return out
