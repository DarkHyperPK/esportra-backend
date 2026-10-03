"""Valorant scoreboard: image -> structured, confidence-scored result."""

from __future__ import annotations

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

PARSER_VERSION = "valorant-scoreboard-v1"


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


def fill_missing_cells(image: np.ndarray, engine: OcrEngine, rows: list[Row], columns: list[Column], pitch: float) -> None:
    """The detector sometimes skips a lone thin digit ("1"). Re-read empty cells with recognition only."""
    centers = [c.xc for c in columns]
    gaps = [b - a for a, b in zip(centers, centers[1:])]
    half_w = 0.45 * min(gaps) if gaps else 40.0
    height, width = image.shape[:2]
    for row in rows:
        for column in columns:
            if column.key not in _SINGLE_VALUE or column.key in row.values:
                continue
            x0, x1 = int(max(0, column.xc - half_w)), int(min(width, column.xc + half_w))
            y0, y1 = int(max(0, row.yc - 0.3 * pitch)), int(min(height, row.yc + 0.3 * pitch))
            text, conf = engine.recognize(image[y0:y1, x0:x1])
            digits = text.strip()
            if digits.isdigit() and len(digits) <= 2:
                row.values[column.key] = (float(digits), conf * 0.8)


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
    fill_missing_cells(image_bgr, engine, rows, columns, pitch)
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
