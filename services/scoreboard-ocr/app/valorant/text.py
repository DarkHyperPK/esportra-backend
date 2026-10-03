"""Text normalization and number parsing tuned for OCR output."""

from __future__ import annotations

import re
import unicodedata

# OCR look-alikes inside numeric cells only (never applied to names).
_DIGIT_CONFUSABLES = str.maketrans({"O": "0", "o": "0", "D": "0", "I": "1", "l": "1", "|": "1", "S": "5", "B": "8", "Z": "2"})
_NUMBER_RE = re.compile(r"^[+-]?\d+(?:[.,]\d+)?$")

# Cyrillic/Greek glyphs players use as "stylized" Latin letters (e.g. MЯNOЪODY).
_NAME_CONFUSABLES = str.maketrans(
    {
        "а": "a", "в": "b", "е": "e", "ё": "e", "к": "k", "м": "m", "н": "h", "о": "o",
        "р": "p", "с": "c", "т": "t", "у": "y", "х": "x", "я": "r", "ъ": "b", "ь": "b",
        "и": "n", "п": "n", "д": "a", "з": "3", "ч": "4", "α": "a", "β": "b", "ε": "e",
        "ι": "i", "κ": "k", "ν": "v", "ο": "o", "ρ": "p", "τ": "t", "υ": "u", "χ": "x",
    }
)


def normalize_header(text: str) -> str:
    upper = unicodedata.normalize("NFKC", text).upper().replace(".", "")
    return re.sub(r"\s+", " ", upper).strip()


def normalize_name(text: str) -> str:
    base = unicodedata.normalize("NFKC", text).casefold().split("#", 1)[0]
    mapped = base.translate(_NAME_CONFUSABLES)
    return re.sub(r"[^0-9a-z]", "", mapped)


def parse_number(text: str) -> tuple[float | None, float]:
    """Return (value, confidence multiplier). Multiplier < 1 when look-alikes were replaced."""
    raw = text.strip().rstrip("%").replace(" ", "")
    if not raw:
        return None, 0.0
    if _NUMBER_RE.match(raw):
        return float(raw.replace(",", ".")), 1.0
    fixed = raw.translate(_DIGIT_CONFUSABLES)
    if fixed != raw and _NUMBER_RE.match(fixed):
        return float(fixed.replace(",", ".")), 0.75
    return None, 0.0


def split_kda(text: str) -> list[tuple[float | None, float]] | None:
    parts = [p for p in re.split(r"\s*/\s*", text.strip()) if p]
    if len(parts) != 3:
        return None
    return [parse_number(p) for p in parts]


def looks_numeric(text: str) -> bool:
    return parse_number(text)[0] is not None or split_kda(text) is not None
