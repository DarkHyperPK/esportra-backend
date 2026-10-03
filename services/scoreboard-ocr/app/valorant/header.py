"""Read the summary above the table: outcome, round score and map name."""

from __future__ import annotations

import re
from dataclasses import dataclass

import numpy as np
from rapidfuzz import fuzz

from app.engine import OcrEngine, OcrToken
from app.models import MapRef, OcrField
from app.valorant.text import normalize_name, parse_number

_SCORE_RE = re.compile(r"^\s*(\d{1,2})\s*[-:–—]\s*(\d{1,2})\s*$")
_OUTCOMES = {"VICTORY": "victory", "DEFEAT": "defeat", "DRAW": "draw"}


@dataclass(frozen=True)
class HeaderRead:
    outcome: OcrField[str]
    ally_score: OcrField[int]
    enemy_score: OcrField[int]
    map: OcrField[MapRef]


def read_outcome(tokens: list[OcrToken]) -> OcrField[str]:
    best: tuple[float, str | None, float] = (0.0, None, 0.0)
    for token in tokens:
        text = token.text.upper().replace(" ", "")
        for word, value in _OUTCOMES.items():
            score = fuzz.ratio(text, word)
            if score >= 80 and score * token.h > best[0]:
                best = (score * token.h, value, token.conf * score / 100)
    return OcrField[str](value=best[1], confidence=round(best[2], 3))


def _score_pair(tokens: list[OcrToken]) -> tuple[int, int, float] | None:
    combined = [t for t in tokens if _SCORE_RE.match(t.text)]
    if combined:
        token = max(combined, key=lambda t: t.h)
        left, right = _SCORE_RE.match(token.text).groups()  # type: ignore[union-attr]
        return int(left), int(right), token.conf
    numbers = [t for t in tokens if parse_number(t.text)[0] is not None and len(t.text.strip()) <= 2]
    numbers.sort(key=lambda t: t.h, reverse=True)
    for i, a in enumerate(numbers):
        for b in numbers[i + 1 :]:
            similar = abs(a.h - b.h) <= 0.25 * a.h and abs(a.yc - b.yc) <= 0.5 * a.h
            if similar:
                left, right = sorted((a, b), key=lambda t: t.x0)
                lv, rv = parse_number(left.text)[0], parse_number(right.text)[0]
                return int(lv or 0), int(rv or 0), min(a.conf, b.conf) * 0.85
    return None


def read_score(tokens: list[OcrToken], outcome: str | None) -> tuple[OcrField[int], OcrField[int]]:
    pair = _score_pair(tokens)
    if pair is None:
        return OcrField[int](), OcrField[int]()
    left, right, conf = pair
    ally, enemy = left, right
    if outcome == "victory" and left < right or outcome == "defeat" and left > right:
        ally, enemy = right, left  # the outcome banner is authoritative about who won
    elif outcome is None:
        conf *= 0.6  # no banner: we assume the left number belongs to the screenshot owner
    return OcrField[int](value=ally, confidence=round(conf, 3)), OcrField[int](value=enemy, confidence=round(conf, 3))


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


def _outcome_axis(tokens: list[OcrToken], image_width: int) -> float:
    banners = [t for t in tokens if any(fuzz.ratio(t.text.upper().replace(" ", ""), w) >= 80 for w in _OUTCOMES)]
    return max(banners, key=lambda t: t.h).xc if banners else image_width / 2


def recover_missing_score(tokens: list[OcrToken], image_bgr: np.ndarray, engine: OcrEngine) -> list[OcrToken]:
    """The detector can miss a lone thin digit ("1"). The two scores sit symmetrically around the
    outcome banner, so re-read the mirror position of the biggest number we did find."""
    numbers = [t for t in tokens if parse_number(t.text)[0] is not None and len(t.text.strip()) <= 2]
    if not numbers:
        return []
    anchor = max(numbers, key=lambda t: t.h)
    height, width = image_bgr.shape[:2]
    mirror_xc = 2 * _outcome_axis(tokens, width) - anchor.xc
    half_w = max(anchor.w, 0.9 * anchor.h) / 2 + 0.15 * anchor.h
    x0, x1 = int(max(0, mirror_xc - half_w)), int(min(width, mirror_xc + half_w))
    y0, y1 = int(max(0, anchor.y0 - 0.1 * anchor.h)), int(min(height, anchor.y1 + 0.1 * anchor.h))
    if x1 - x0 < 4 or abs(mirror_xc - anchor.xc) < anchor.w:
        return []
    text, conf = engine.recognize(image_bgr[y0:y1, x0:x1])
    if parse_number(text)[0] is None or len(text.strip()) > 2:
        return []
    return [OcrToken(text, conf * 0.85, x0, anchor.y0, x1, anchor.y1)]


def _is_score_like(token: OcrToken) -> bool:
    return parse_number(token.text)[0] is not None and len(token.text.strip()) <= 2


def reverify_scores(tokens: list[OcrToken], image_bgr: np.ndarray, engine: OcrEngine) -> list[OcrToken]:
    """Score digits decide the match, so read each one a second time from a padded crop.
    Agreement keeps the better confidence; disagreement keeps the more confident read, flagged lower."""
    height, width = image_bgr.shape[:2]
    candidates = set(map(id, sorted((t for t in tokens if _is_score_like(t)), key=lambda t: t.h, reverse=True)[:4]))
    out: list[OcrToken] = []
    for token in tokens:
        if id(token) not in candidates:
            out.append(token)
            continue
        pad = 0.2 * token.h
        x0, x1 = int(max(0, token.x0 - pad)), int(min(width, token.x1 + pad))
        y0, y1 = int(max(0, token.y0 - pad)), int(min(height, token.y1 + pad))
        text, conf = engine.recognize(image_bgr[y0:y1, x0:x1])
        if text.strip() == token.text.strip():
            out.append(OcrToken(token.text, max(conf, token.conf), token.x0, token.y0, token.x1, token.y1))
        elif parse_number(text)[0] is not None and len(text.strip()) <= 2 and conf > token.conf:
            out.append(OcrToken(text.strip(), conf * 0.7, token.x0, token.y0, token.x1, token.y1))
        else:
            out.append(OcrToken(token.text, token.conf * 0.7, token.x0, token.y0, token.x1, token.y1))
    return out


def read_header(
    tokens: list[OcrToken], header_top: float, maps: list[MapRef], image_bgr: np.ndarray, engine: OcrEngine
) -> HeaderRead:
    above = reverify_scores([t for t in tokens if t.y1 <= header_top], image_bgr, engine)
    outcome = read_outcome(above)
    ally, enemy = read_score(above, outcome.value)
    if ally.value is None:
        ally, enemy = read_score(above + recover_missing_score(above, image_bgr, engine), outcome.value)
    return HeaderRead(outcome=outcome, ally_score=ally, enemy_score=enemy, map=read_map(above, maps))
