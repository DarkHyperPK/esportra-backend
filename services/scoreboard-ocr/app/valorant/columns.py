"""Locate the scoreboard header row and its stat columns."""

from __future__ import annotations

from dataclasses import dataclass
from statistics import median

from rapidfuzz import fuzz

from app.engine import OcrToken
from app.valorant.text import normalize_header

COLUMN_ALIASES: dict[str, tuple[str, ...]] = {
    "acs": ("ACS", "AVG COMBAT SCORE", "AVERAGE COMBAT SCORE", "COMBAT SCORE"),
    "kda": ("K / D / A", "K/D/A", "KDA"),
    "kills": ("K", "KILLS"),
    "deaths": ("D", "DEATHS"),
    "assists": ("A", "ASSISTS"),
    "econ": ("ECON RATING", "ECON"),
    "firstBloods": ("FIRST BLOODS", "FIRST BLOOD", "FIRST KILLS", "FB", "FK"),
    "plants": ("PLANTS",),
    "defuses": ("DEFUSES",),
    "hsPct": ("HS%", "HS %", "HEADSHOT %", "HEADSHOT%"),
    "adr": ("ADR",),
}

MIN_COLUMNS = 3


@dataclass(frozen=True)
class Column:
    key: str
    x0: float
    x1: float
    y0: float
    y1: float

    @property
    def xc(self) -> float:
        return (self.x0 + self.x1) / 2

    @property
    def yc(self) -> float:
        return (self.y0 + self.y1) / 2


@dataclass(frozen=True)
class _Candidate:
    tokens: tuple[int, ...]
    text: str
    x0: float
    x1: float
    y0: float
    y1: float


def match_alias(text: str) -> tuple[str | None, float]:
    norm = normalize_header(text)
    compact = norm.replace(" ", "")
    best_key, best_score = None, 0.0
    for key, aliases in COLUMN_ALIASES.items():
        for alias in aliases:
            alias_compact = alias.replace(" ", "")
            if len(alias_compact) <= 3:
                score = 100.0 if compact == alias_compact else 0.0
            else:
                score = fuzz.ratio(compact, alias_compact)
            if score > best_score:
                best_key, best_score = key, score
    return (best_key, best_score) if best_score >= 85 else (None, 0.0)


def _candidates(tokens: list[OcrToken]) -> list[_Candidate]:
    out = [_Candidate((i,), t.text, t.x0, t.x1, t.y0, t.y1) for i, t in enumerate(tokens)]
    for i, a in enumerate(tokens):
        for j, b in enumerate(tokens):
            if i == j:
                continue
            h = max(a.h, b.h)
            same_line = abs(a.yc - b.yc) < 0.5 * h and 0 <= b.x0 - a.x1 < 1.0 * h
            overlap = min(a.x1, b.x1) - max(a.x0, b.x0)
            stacked = overlap > 0.4 * min(a.w, b.w) and 0 <= b.y0 - a.y1 < 0.9 * h
            if same_line or stacked:
                out.append(
                    _Candidate((i, j), f"{a.text} {b.text}", min(a.x0, b.x0), max(a.x1, b.x1), min(a.y0, b.y0), max(a.y1, b.y1))
                )
    return out


def find_header(tokens: list[OcrToken]) -> list[Column]:
    """Return the stat columns of the best header band, left to right (empty if none)."""
    scored: list[tuple[float, _Candidate, str]] = []
    for cand in _candidates(tokens):
        key, score = match_alias(cand.text)
        if key:
            scored.append((score + len(cand.tokens) * 0.5, cand, key))
    scored.sort(key=lambda item: item[0], reverse=True)

    used: set[int] = set()
    cells: list[Column] = []
    for _, cand, key in scored:
        if used.intersection(cand.tokens):
            continue
        used.update(cand.tokens)
        cells.append(Column(key, cand.x0, cand.x1, cand.y0, cand.y1))
    return _best_band(cells)


def _best_band(cells: list[Column]) -> list[Column]:
    if not cells:
        return []
    line_h = median(c.y1 - c.y0 for c in cells)
    best: list[Column] = []
    for anchor in cells:
        band = [c for c in cells if abs(c.yc - anchor.yc) <= 1.2 * line_h]
        by_key: dict[str, Column] = {}
        for cell in sorted(band, key=lambda c: abs(c.yc - anchor.yc)):
            by_key.setdefault(cell.key, cell)
        if len(by_key) > len(best):
            best = list(by_key.values())
    if len(best) < MIN_COLUMNS:
        return []
    return sorted(best, key=lambda c: c.xc)
