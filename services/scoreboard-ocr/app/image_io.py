"""Safe image decoding: format allow-list, pixel cap, normalized width."""

from __future__ import annotations

import io

import cv2
import numpy as np
from PIL import Image, UnidentifiedImageError

ALLOWED_FORMATS = {"PNG", "JPEG", "WEBP"}
MAX_PIXELS = 3840 * 2160  # 4K
TARGET_WIDTH = 1920
TARGET_HEIGHT = 1080
MAX_WORK_WIDTH = 3200

Image.MAX_IMAGE_PIXELS = MAX_PIXELS  # decompression-bomb guard


class ImageRejected(ValueError):
    """The upload is not an image we are willing to process."""


def decode_image(data: bytes) -> np.ndarray:
    """Decode bytes into a BGR array scaled to TARGET_WIDTH. Raises ImageRejected."""
    try:
        with Image.open(io.BytesIO(data)) as probe:
            fmt = probe.format
            width, height = probe.size
    except (UnidentifiedImageError, Image.DecompressionBombError, OSError) as exc:
        raise ImageRejected("File is not a readable image.") from exc

    if fmt not in ALLOWED_FORMATS:
        raise ImageRejected("Only PNG, JPEG or WebP screenshots are supported.")
    if width * height > MAX_PIXELS:
        raise ImageRejected("Screenshot resolution is above 4K.")
    if width < 640 or height < 360:
        raise ImageRejected("Screenshot is too small to read.")

    with Image.open(io.BytesIO(data)) as img:
        rgb = np.asarray(img.convert("RGB"))
    bgr = cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)
    return normalize_scale(trim_black_bars(bgr))


def trim_black_bars(image_bgr: np.ndarray, threshold: int = 12) -> np.ndarray:
    """Remove pillarbox/letterbox bars (a 4:3 or 5:4 game captured on a 16:9 screen), so the game area
    scales like a native capture. Only uniform near-black edges are cut; the game itself is never black."""
    bright = image_bgr.max(axis=2) > threshold
    cols, rows = np.flatnonzero(bright.mean(axis=0) > 0.01), np.flatnonzero(bright.mean(axis=1) > 0.01)
    if cols.size == 0 or rows.size == 0:
        return image_bgr
    x0, x1, y0, y1 = cols[0], cols[-1] + 1, rows[0], rows[-1] + 1
    height, width = image_bgr.shape[:2]
    if (x1 - x0) * (y1 - y0) > 0.97 * width * height or x1 - x0 < 640 or y1 - y0 < 360:
        return image_bgr  # no real bars, or what's left is too small to be a game frame
    return image_bgr[y0:y1, x0:x1]


def normalize_scale(image_bgr: np.ndarray) -> np.ndarray:
    """Valorant scales its UI with screen height on wide screens and with width on narrow ones.
    Scaling so the image is at least 1920 wide or 1080 tall (whichever is larger) keeps text the same
    size for 16:9, 16:10 and ultrawide captures."""
    height, width = image_bgr.shape[:2]
    scale = min(max(TARGET_WIDTH / width, TARGET_HEIGHT / height), MAX_WORK_WIDTH / width)
    if abs(scale - 1) < 0.01:
        return image_bgr
    interpolation = cv2.INTER_AREA if scale < 1 else cv2.INTER_CUBIC
    return cv2.resize(image_bgr, (round(width * scale), round(height * scale)), interpolation=interpolation)
