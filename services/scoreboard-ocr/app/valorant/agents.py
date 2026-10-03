"""Identify the agent from the portrait at the start of each row.

The exact portrait position differs between resolutions and UI versions, so instead of a
fixed crop we search a generous region left of the player name with every agent icon at a
few scales (normalized cross-correlation on colour), and keep the best hit per agent.
"""

from __future__ import annotations

import cv2
import numpy as np

from app.models import AgentRef, OcrField
from app.valorant.catalog import AgentTemplate

SCALES = (0.7, 0.8, 0.9, 1.0)
MIN_SCORE = 0.45


def portrait_region(image_bgr: np.ndarray, name_x0: float, yc: float, pitch: float) -> np.ndarray:
    """Area left of the player name, a little taller than one row."""
    height, width = image_bgr.shape[:2]
    x0, x1 = int(max(0, name_x0 - 1.9 * pitch)), int(min(width, name_x0 - 0.02 * pitch))
    y0, y1 = int(max(0, yc - 0.6 * pitch)), int(min(height, yc + 0.6 * pitch))
    if x1 - x0 < 8 or y1 - y0 < 8:
        return np.zeros((0, 0, 3), dtype=np.uint8)
    return image_bgr[y0:y1, x0:x1]


def _composite(icon_bgra: np.ndarray, size: int, background_bgr: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    icon = cv2.resize(icon_bgra, (size, size), interpolation=cv2.INTER_AREA).astype(np.float32)
    alpha = icon[..., 3:4] / 255.0
    composite = icon[..., :3] * alpha + background_bgr.astype(np.float32) * (1 - alpha)
    return composite.astype(np.uint8), alpha[..., 0] > 0.5


def _colour_similarity(patch: np.ndarray, template: np.ndarray, mask: np.ndarray) -> float:
    if mask.sum() < 20:
        return 0.0
    diff = np.abs(patch.astype(np.float32) - template.astype(np.float32))[mask]
    return float(np.clip(1 - diff.mean() / 96.0, 0, 1))


def _best_hit(region: np.ndarray, icon_bgra: np.ndarray, pitch: float, background: np.ndarray) -> float:
    """Shape (normalized correlation) and colour must both agree at the best location."""
    best = -1.0
    for scale in SCALES:
        size = int(scale * pitch)
        if size < 12 or size > region.shape[0] or size > region.shape[1]:
            continue
        template, mask = _composite(icon_bgra, size, background)
        result = cv2.matchTemplate(region, template, cv2.TM_CCOEFF_NORMED)
        _, ncc, _, (x, y) = cv2.minMaxLoc(result)
        colour = _colour_similarity(region[y : y + size, x : x + size], template, mask)
        best = max(best, 0.5 * float(ncc) + 0.5 * colour)
    return best


def identify_agent(region_bgr: np.ndarray, templates: list[AgentTemplate], pitch: float) -> OcrField[AgentRef]:
    if region_bgr.size == 0 or not templates or pitch <= 0:
        return OcrField[AgentRef]()
    border = np.concatenate([region_bgr[0], region_bgr[-1], region_bgr[:, 0], region_bgr[:, -1]])
    background = np.median(border, axis=0)
    scores = sorted(((_best_hit(region_bgr, t.icon_bgra, pitch, background), t.ref) for t in templates), key=lambda s: s[0], reverse=True)
    best, ref = scores[0]
    if best < MIN_SCORE:
        return OcrField[AgentRef]()
    second = scores[1][0] if len(scores) > 1 else 0.0
    confidence = float(np.clip(best, 0, 1) * np.clip(0.5 + (best - second) * 4, 0, 1))
    return OcrField[AgentRef](value=ref, confidence=round(confidence, 3))
