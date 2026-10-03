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
from app.valorant.rows import Row, build_rows, row_pitch
from app.valorant.sides import classify_row

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


def _player(image: np.ndarray, row: Row, columns: list[Column], pitch: float, catalog: Catalog) -> PlayerRow:
    row_x0 = row.name_x0 - pitch
    side, side_conf = classify_row(image, row_x0, max(c.x1 for c in columns), row.yc, pitch)
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
    players = [_player(image_bgr, row, columns, pitch, catalog) for row in rows]
    matches = match_names([row.name for row in rows], rosters)
    players = [p.model_copy(update={"roster_match": m}) for p, m in zip(players, matches)]
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
