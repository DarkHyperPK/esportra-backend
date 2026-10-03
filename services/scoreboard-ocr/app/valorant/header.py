"""Read the summary above the table: outcome banner, round score and map name.

Valorant shows the summary as one line, "<your rounds> VICTORY|DEFEAT <their rounds>", with the
screenshot owner's score on the left. The match length ("34:57") and date sit elsewhere and must
never be mistaken for the score.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from itertools import product

import numpy as np
from rapidfuzz import fuzz

from app.engine import OcrEngine, OcrToken
from app.models import MapRef, OcrField
from app.valorant.text import normalize_name

_OUTCOMES = {"VICTORY": "victory", "DEFEAT": "defeat", "DRAW": "draw"}
_DIGITS = re.compile(r"\d{1,2}")


@dataclass(frozen=True)
class HeaderRead:
    outcome: OcrField[str]
    ally_score: OcrField[int]
    enemy_score: OcrField[int]
    map: OcrField[MapRef]


@dataclass(frozen=True)
class _Banner:
    token: OcrToken
    outcome: str
    confidence: float
    left_digits: str  # digits glued to the banner token, e.g. "8 DEFEAT"
    right_digits: str


def find_banner(tokens: list[OcrToken]) -> _Banner | None:
    best: tuple[float, _Banner | None] = (0.0, None)
    for token in tokens:
        upper = token.text.upper()
        letters = re.sub(r"[^A-Z]", "", upper)
        for word, value in _OUTCOMES.items():
            score = fuzz.ratio(letters, word)
            if score < 80 or score * token.h <= best[0]:
                continue
            first = re.search(r"[A-Z]", upper)
            last = max(i for i, ch in enumerate(upper) if ch.isalpha())
            start = first.start() if first else 0
            left, right = token.text[:start], token.text[last + 1 :]
            banner = _Banner(token, value, token.conf * score / 100, "".join(_DIGITS.findall(left)), "".join(_DIGITS.findall(right)))
            best = (score * token.h, banner)
    return best[1]


def read_outcome(tokens: list[OcrToken]) -> OcrField[str]:
    banner = find_banner(tokens)
    return OcrField[str](value=banner.outcome, confidence=round(banner.confidence, 3)) if banner else OcrField[str]()


def is_valid_final(ally: int, enemy: int, outcome: str | None) -> bool:
    """Valorant: first to 13; from 12-12 overtime is won by two."""
    if outcome == "victory" and ally <= enemy or outcome == "defeat" and ally >= enemy:
        return False
    if outcome == "draw":
        return ally == enemy
    high, low = max(ally, enemy), min(ally, enemy)
    return (high == 13 and low <= 11) or (high >= 14 and high - low == 2)


def _candidates(text: str, conf: float, glued_to_banner: bool) -> list[tuple[int, float]]:
    """Possible numbers in one read. A stray stroke of the banner letter often adds a leading '1'."""
    digits = "".join(_DIGITS.findall(text))[:2]
    if not digits:
        return []
    out = [(int(digits), conf)]
    if glued_to_banner and len(digits) == 2:
        out.append((int(digits[1] if digits[0] == "1" else digits[0]), conf * 0.8))
    return out


def _side_token(tokens: list[OcrToken], banner: OcrToken, left: bool) -> OcrToken | None:
    line = [t for t in tokens if t is not banner and abs(t.yc - banner.yc) < 0.6 * banner.h and _DIGITS.search(t.text)]
    # Detector boxes can overlap ("13" and "3 VICTORY 7"): a token that starts outside the banner counts.
    outside = (lambda t: t.x0 < banner.x0 and t.xc < banner.xc) if left else (lambda t: t.x1 > banner.x1 and t.xc > banner.xc)
    side = [t for t in line if outside(t) and len(re.sub(r"\D", "", t.text)) <= 2]
    if not side:
        return None
    return min(side, key=lambda t: abs(t.xc - banner.xc))


def _crop(image: np.ndarray, x0: float, y0: float, x1: float, y1: float) -> np.ndarray:
    height, width = image.shape[:2]
    return image[int(max(0, y0)) : int(min(height, y1)), int(max(0, x0)) : int(min(width, x1))]


def _side_reads(image: np.ndarray, engine: OcrEngine, banner: _Banner, token: OcrToken | None, left: bool) -> list[tuple[int, float]]:
    b = banner.token
    glued = banner.left_digits if left else banner.right_digits
    reads: list[tuple[int, float]] = []
    if glued:
        reads += _candidates(glued, b.conf * 0.9, glued_to_banner=False)
    if token is not None:
        near = (b.x0 - token.x1 if left else token.x0 - b.x1) < 0.15 * b.h
        reads += _candidates(token.text, token.conf, glued_to_banner=near)
        pad = 0.3 * token.h
        text, conf = engine.recognize(_crop(image, token.x0 - pad, token.y0 - pad, token.x1 + pad, token.y1 + pad))
        reads += _candidates(text, conf, glued_to_banner=True)
    if not reads:  # detector missed the digit: read the area right next to the banner
        x0, x1 = (b.x0 - 1.6 * b.h, b.x0) if left else (b.x1, b.x1 + 1.6 * b.h)
        text, conf = engine.recognize(_crop(image, x0, b.y0, x1, b.y1))
        reads += [(v, c * 0.85) for v, c in _candidates(text, conf, glued_to_banner=True)]
    return reads


def read_score(tokens: list[OcrToken], image: np.ndarray, engine: OcrEngine) -> tuple[OcrField[int], OcrField[int]]:
    banner = find_banner(tokens)
    if banner is None:
        return OcrField[int](), OcrField[int]()
    ally_reads = _side_reads(image, engine, banner, _side_token(tokens, banner.token, left=True), left=True)
    enemy_reads = _side_reads(image, engine, banner, _side_token(tokens, banner.token, left=False), left=False)
    pairs = sorted(product(ally_reads, enemy_reads), key=lambda p: p[0][1] * p[1][1], reverse=True)
    if not pairs:
        return OcrField[int](), OcrField[int]()
    valid = [p for p in pairs if is_valid_final(p[0][0], p[1][0], banner.outcome)]
    (ally, a_conf), (enemy, e_conf) = valid[0] if valid else pairs[0]
    penalty = 1.0 if valid else 0.5
    return (
        OcrField[int](value=ally, confidence=round(a_conf * penalty, 3)),
        OcrField[int](value=enemy, confidence=round(e_conf * penalty, 3)),
    )


def read_map(tokens: list[OcrToken], maps: list[MapRef]) -> OcrField[MapRef]:
    best: tuple[float, MapRef | None, float] = (0.0, None, 0.0)
    for token in tokens:
        text = normalize_name(token.text)
        if len(text) < 4:
            continue
        for candidate in maps:
            name = normalize_name(candidate.name)
            score = 95.0 if len(name) >= 4 and name in text else fuzz.ratio(text, name)
            if score >= 85 and score > best[0]:
                best = (score, candidate, token.conf * score / 100)
    return OcrField[MapRef](value=best[1], confidence=round(best[2], 3))


def read_header(
    tokens: list[OcrToken], header_top: float, maps: list[MapRef], image_bgr: np.ndarray, engine: OcrEngine
) -> HeaderRead:
    above = [t for t in tokens if t.y1 <= header_top]
    ally, enemy = read_score(above, image_bgr, engine)
    return HeaderRead(outcome=read_outcome(above), ally_score=ally, enemy_score=enemy, map=read_map(above, maps))
