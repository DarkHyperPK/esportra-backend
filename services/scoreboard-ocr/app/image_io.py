"""Safe image decoding: format allow-list, pixel cap, normalized width."""

from __future__ import annotations

import io

import cv2
import numpy as np
from PIL import Image, UnidentifiedImageError

ALLOWED_FORMATS = {"PNG", "JPEG", "WEBP"}
MAX_PIXELS = 3840 * 2160  # 4K
TARGET_WIDTH = 1920

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
    return normalize_width(bgr)


def normalize_width(image_bgr: np.ndarray, target: int = TARGET_WIDTH) -> np.ndarray:
    height, width = image_bgr.shape[:2]
    if width == target:
        return image_bgr
    scale = target / width
    interpolation = cv2.INTER_AREA if scale < 1 else cv2.INTER_CUBIC
    return cv2.resize(image_bgr, (target, round(height * scale)), interpolation=interpolation)
