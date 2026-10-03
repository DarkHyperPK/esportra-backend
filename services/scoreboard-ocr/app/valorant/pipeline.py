"""Valorant scoreboard: image -> structured, confidence-scored result."""

from __future__ import annotations

import cv2
import numpy as np

from app.engine import OcrEngine
from app.models import STAT_KEYS, OcrField, PlayerRow, Rosters, ScoreboardResult, ScoreboardWarning
from app.valorant.agents import identify_agent, portrait_region
from app.valorant.catalog import Catalog
from app.valorant.checks import check_kills, check_rows, check_score
from app.valorant.columns import Column, find_header
from app.valorant.header import read_header
from app.valorant.names import match_names, resolve_ally_team
from app.valorant.rows import Row, build_rows, row_pitch, stat_boundary
from app.valorant.sides import classify_row, fill_last_unknown
from app.valorant.text import split_kda

PARSER_VERSION = "valorant-scoreboard-v2"


class ScoreboardNotFound(ValueError):
    """No scoreboard table could be located in the image."""


def _stats(row: Row, columns: list[Column]) -> dict[str, OcrField[float]]:
    found = {c.key for c in columns}
    if "kda" in found:
        found |= {"kills", "deaths", "assists"}
    out: dict[str, OcrField[float]] = {}
    for key in STAT_KEYS:
        if key not in found:
            continue
        value = row.values.get(key)
        out[key] = OcrField[float](value=value[0], confidence=round(value[1], 3)) if value else OcrField[float]()
    return out


_SINGLE_VALUE = {"firstBloods", "plants", "defuses", "acs", "econ", "hsPct", "adr"}
_KDA_KEYS = ("kills", "deaths", "assists")
LOW_CONFIDENCE = 0.6


def _recognize(engine: OcrEngine, crop: np.ndarray) -> tuple[str, float]:
    """Recognition only; small crops are enlarged first because the recogniser reads ~48px text best."""
    if crop.size == 0:
        return "", 0.0
    if crop.shape[0] < 48:
        scale = 48 / crop.shape[0]
        crop = cv2.resize(crop, None, fx=scale, fy=scale, interpolation=cv2.INTER_CUBIC)
    return engine.recognize(crop)


def _merge(current: tuple[float, float] | None, value: float, conf: float) -> tuple[float, float]:
    """Keep the more confident read; two reads that agree are trusted more."""
    if current is None:
        return value, conf
    if current[0] == value:
        return value, max(current[1], conf, min(1.0, (current[1] + conf) / 2 + 0.2))
    return (value, conf) if conf > current[1] else current


def _reread_kda(image: np.ndarray, engine: OcrEngine, row: Row, box: tuple[int, int, int, int]) -> None:
    """K/D/A is replaced as a whole triple, never mixed cell by cell with the first read."""
    current = [row.values.get(k) for k in _KDA_KEYS]
    if all(v is not None and v[1] >= LOW_CONFIDENCE for v in current):
        return
    x0, y0, x1, y1 = box
    text, conf = _recognize(engine, image[y0:y1, x0:x1])
    parts = split_kda(text)
    if not parts or any(value is None for value, _ in parts):
        return
    reread = [(float(value), conf * mult) for value, mult in parts if value is not None]
    if all(v is not None for v in current) and [v[0] for v in current if v] == [v for v, _ in reread]:
        for key, cur, (value, c) in zip(_KDA_KEYS, current, reread):
            row.values[key] = _merge(cur, value, c)
        return
    current_conf = min((v[1] for v in current if v is not None), default=0.0) if all(v is not None for v in current) else 0.0
    if min(c for _, c in reread) > current_conf:
        for key, (value, c) in zip(_KDA_KEYS, reread):
            row.values[key] = (value, c)


def _reread_single(image: np.ndarray, engine: OcrEngine, row: Row, key: str, box: tuple[int, int, int, int]) -> None:
    current = row.values.get(key)
    if current is not None and current[1] >= LOW_CONFIDENCE:
        return
    x0, y0, x1, y1 = box
    text, conf = _recognize(engine, image[y0:y1, x0:x1])
    digits = text.strip()
    if digits.isdigit() and len(digits) <= 4:
        row.values[key] = _merge(current, float(digits), conf * (0.8 if current is None else 1.0))


def recover_cells(image: np.ndarray, engine: OcrEngine, rows: list[Row], columns: list[Column], pitch: float) -> None:
    """Second pass over each row: re-read cells that are empty or below confidence, one cell at a time.
    The full-screen detector sometimes skips a lone "1" or splits "20 / 7 / 3"; a tight crop fixes most of it."""
    centers = [c.xc for c in columns]
    gaps = [b - a for a, b in zip(centers, centers[1:])]
    half_w = 0.45 * min(gaps) if gaps else 40.0
    height, width = image.shape[:2]
    for row in rows:
        y0, y1 = int(max(0, row.yc - 0.32 * pitch)), int(min(height, row.yc + 0.32 * pitch))
        for column in columns:
            box = (int(max(0, column.xc - half_w)), y0, int(min(width, column.xc + half_w)), y1)
            if column.key == "kda":
                _reread_kda(image, engine, row, box)
            elif column.key in _SINGLE_VALUE:
                _reread_single(image, engine, row, column.key, box)


def _player(image: np.ndarray, row: Row, columns: list[Column], pitch: float, catalog: Catalog) -> PlayerRow:
    side, side_conf = classify_row(image, stat_boundary(columns), max(c.x1 for c in columns), row.yc, pitch)
    agent = identify_agent(portrait_region(image, row.name_x0, row.yc, pitch), catalog.agents, pitch)
    return PlayerRow(
        side=side,
        side_confidence=side_conf,
        name=OcrField[str](value=row.name or None, confidence=round(row.name_conf, 3)),
        agent=agent,
        stats=_stats(row, columns),
    )


def parse_scoreboard(image_bgr: np.ndarray, engine: OcrEngine, catalog: Catalog, rosters: Rosters) -> ScoreboardResult:
    tokens = engine.read(image_bgr)
    columns = find_header(tokens)
    if not columns:
        raise ScoreboardNotFound("Could not find the scoreboard columns. Upload the in-game Scoreboard tab.")
    rows = build_rows(tokens, columns)
    if not rows:
        raise ScoreboardNotFound("Found the scoreboard header but no player rows.")

    pitch = row_pitch(rows)
    recover_cells(image_bgr, engine, rows, columns, pitch)
    players = [_player(image_bgr, row, columns, pitch, catalog) for row in rows]
    filled = fill_last_unknown([p.side for p in players])
    players = [p if conf < 0 else p.model_copy(update={"side": side, "side_confidence": conf}) for p, (side, conf) in zip(players, filled)]
    named = match_names([row.name for row in rows], rosters)
    matches = [match for match, _ in named]
    players = [
        p.model_copy(update={"roster_match": match, "name": p.name.model_copy(update={"value": shown or None})})
        for p, (match, shown) in zip(players, named)
    ]
    ally_team, ally_conf = resolve_ally_team([p.side for p in players], matches)

    header = read_header(tokens, min(c.y0 for c in columns), catalog.maps, image_bgr, engine)
    warnings = [*check_score(header.ally_score.value, header.enemy_score.value), *check_rows(players), *check_kills(players)]
    if ally_team is None:
        warnings.append(ScoreboardWarning(code="TEAM_UNKNOWN", message="Could not tell which bracket team is the screenshot owner's side."))

    height, width = image_bgr.shape[:2]
    return ScoreboardResult(
        parser_version=PARSER_VERSION,
        engine_version=engine.version,
        image_width=width,
        image_height=height,
        outcome=header.outcome,
        ally_score=header.ally_score,
        enemy_score=header.enemy_score,
        map=header.map,
        ally_team=OcrField(value=ally_team, confidence=ally_conf),
        columns=sorted({k for p in players for k in p.stats}),
        players=players,
        warnings=warnings,
    )
