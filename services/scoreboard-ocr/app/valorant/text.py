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


def _slash_as_one(parts: list[str]) -> list[list[str]]:
    """OCR often reads the '/' between K, D and A as '1'. Re-split parts on a '1' until there are three
    parts of one or two digits each. Returns every interpretation that works."""
    if len(parts) == 3:
        return [parts] if all(1 <= len(p) <= 2 for p in parts) else []
    if len(parts) > 3:
        return []
    found: list[list[str]] = []
    for index, part in enumerate(parts):
        for pos, ch in enumerate(part):
            if ch == "1" and 0 < pos < len(part) - 1:
                found += _slash_as_one(parts[:index] + [part[:pos], part[pos + 1 :]] + parts[index + 1 :])
    unique: list[list[str]] = []
    for option in found:
        if option not in unique:
            unique.append(option)
    return unique


def split_kda(text: str) -> list[tuple[float | None, float]] | None:
    """Parse "K / D / A". Tolerates dropped spaces, '|' for '/', and slashes misread as '1'
    (the last case comes back with low confidence so the captain checks it)."""
    parts = [p for p in re.split(r"\s*[/|\\]\s*|\s+", text.strip()) if p]
    if len(parts) == 3 and all(parse_number(p)[0] is not None for p in parts):
        parsed = [parse_number(p) for p in parts]
        # K, D and A are never above 99 in one map; a bigger number means separators were misread.
        if all(v is not None and v <= 99 for v, _ in parsed):
            return parsed
    if not all(p.isdigit() for p in parts):
        return None
    options = _slash_as_one(parts)
    if not options:
        return None
    confidence = 0.45 if len(options) == 1 else 0.25
    return [(float(p), confidence) for p in options[0]]
