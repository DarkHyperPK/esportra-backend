"""Tell ally rows from enemy rows by their tint (teal/yellow = ally, red = enemy)."""

from __future__ import annotations

import cv2
import numpy as np

from app.models import Side

# Hue ranges in degrees (0..360).
_ALLY_RANGES = ((140.0, 215.0), (35.0, 75.0))  # teal/green team rows, gold "you" row
_ENEMY_RANGES = ((330.0, 360.0), (0.0, 22.0))  # red rows


def _in_ranges(hue: np.ndarray, ranges: tuple[tuple[float, float], ...]) -> np.ndarray:
    mask = np.zeros(hue.shape, dtype=bool)
    for low, high in ranges:
        mask |= (hue >= low) & (hue <= high)
    return mask


def classify_band(band_bgr: np.ndarray) -> tuple[Side | None, float]:
    """Classify a horizontal strip of one scoreboard row."""
    if band_bgr.size == 0:
        return None, 0.0
    hsv = cv2.cvtColor(band_bgr, cv2.COLOR_BGR2HSV)
    hue = hsv[..., 0].astype(np.float32) * 2.0
    sat, val = hsv[..., 1], hsv[..., 2]
    tinted = (sat >= 50) & (val >= 40)  # skip white text, black outlines and grey UI
    total = int(tinted.sum())
    if total < 0.05 * hue.size:
        return None, 0.0
    ally = int((_in_ranges(hue, _ALLY_RANGES) & tinted).sum())
    enemy = int((_in_ranges(hue, _ENEMY_RANGES) & tinted).sum())
    if ally == enemy:
        return None, 0.0
    side: Side = "ally" if ally > enemy else "enemy"
    winner = max(ally, enemy)
    return side, round(winner / total, 3)


def classify_row(image_bgr: np.ndarray, x0: float, x1: float, yc: float, pitch: float) -> tuple[Side | None, float]:
    height, width = image_bgr.shape[:2]
    top = int(max(0, yc - 0.35 * pitch))
    bottom = int(min(height, yc + 0.35 * pitch))
    left = int(max(0, x0))
    right = int(min(width, x1))
    return classify_band(image_bgr[top:bottom, left:right])
