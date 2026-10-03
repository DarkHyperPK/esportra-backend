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
    return normalize_scale(bgr)


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
