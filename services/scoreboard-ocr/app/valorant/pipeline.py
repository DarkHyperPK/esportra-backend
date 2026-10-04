"""Valorant scoreboard: image -> structured, confidence-scored result."""

from __future__ import annotations

import re

import cv2
import numpy as np

from app.engine import OcrEngine
from app.models import STAT_KEYS, OcrField, PlayerRow, Rosters, ScoreboardResult, ScoreboardWarning, TeamSlot
from app.valorant.agents import identify_agent
from app.valorant.catalog import Catalog
from app.valorant.checks import check_hidden_names, check_kills, check_rows, check_score
from app.valorant.columns import Column, find_header
from app.valorant.header import read_header
from app.image_io import normalize_scale
from app.valorant.layout import STRETCH_TOLERANCE, estimate_stretch, portrait_box, portrait_stretch, table_left, unstretch
from app.valorant.names import match_by_elimination, match_names, resolve_ally_team
from app.valorant.name_reread import recover_names
from app.valorant.rows import Row, build_rows, row_pitch, stat_boundary
from app.valorant.sides import classify_row, fill_last_unknown, relative_sides, row_tint
from app.valorant.text import normalize_numeric, split_kda

PARSER_VERSION = "valorant-scoreboard-v3"


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


_SINGLE_VALUE = {"firstBloods", "plants", "defuses", "acs", "econ", "hsPct", "adr", "trades"}
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


def _reread_single(
    image: np.ndarray, engine: OcrEngine, row: Row, key: str, box: tuple[int, int, int, int], after_name: bool = False
) -> None:
    current = row.values.get(key)
    if current is not None and current[1] >= LOW_CONFIDENCE:
        return
    x0, y0, x1, y1 = box
    text, conf = _recognize(engine, image[y0:y1, x0:x1])
    digits = normalize_numeric(text).strip()
    if after_name:
        # The first column can sit under the tail of a long name ("Rawzu from temu394"): keep the
        # trailing number. When a letter runs straight into one or two digits ("One60" for 260), the
        # name is covering the number's first digit: leave the cell for the captain instead of guessing.
        if re.search(r"[^\W\d_]\d{1,2}$", digits):
            return
        tail = re.search(r"(\d{2,4})$", digits)
        digits = tail.group(1) if tail else digits
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
                _reread_single(image, engine, row, column.key, box, after_name=column is columns[0])


def _portraits(image: np.ndarray, rows: list[Row], pitch: float) -> list[np.ndarray]:
    """Each row's agent portrait, located from the table's left edge (empty when it can't be found)."""
    starts = [r.name_x0 for r in rows if r.name]
    if not starts:
        return [np.zeros((0, 0, 3), dtype=np.uint8) for _ in rows]
    name_x0 = float(np.median(starts))
    left = table_left(image, [(r.yc - 0.45 * pitch, r.yc + 0.45 * pitch) for r in rows], name_x0, pitch)
    crops = []
    for row in rows:
        box = portrait_box(image, left, name_x0, row.yc, pitch)
        crops.append(image[box.y0 : box.y1, box.x0 : box.x1] if box else np.zeros((0, 0, 3), dtype=np.uint8))
    return crops


def _player(image: np.ndarray, row: Row, columns: list[Column], pitch: float, portrait: np.ndarray, catalog: Catalog) -> PlayerRow:
    side, side_conf = classify_row(image, stat_boundary(columns), max(c.x1 for c in columns), row.yc, pitch)
    return PlayerRow(
        side=side,
        side_confidence=side_conf,
        name=OcrField[str](value=row.name or None, confidence=round(row.name_conf, 3)),
        name_read=row.name or None,
        agent=identify_agent(portrait, catalog, row.agent_hint),
        stats=_stats(row, columns),
    )


def _apply_names(players: list[PlayerRow], rows: list[Row], rosters: Rosters) -> tuple[list[PlayerRow], TeamSlot | None, float]:
    names = [row.name for row in rows]
    sides = [p.side for p in players]
    named = match_names(names, rosters)
    ally_team, ally_conf = resolve_ally_team(sides, [m for m, _ in named])
    named = match_by_elimination(names, sides, named, rosters, ally_team)
    out = []
    for player, (match, shown) in zip(players, named):
        confidence = player.name.confidence if match is None or match.method == "read" else 0.0
        name = OcrField[str](value=shown or None, confidence=round(max(confidence, match.score if match and match.method == "read" else 0.0), 3))
        out.append(player.model_copy(update={"roster_match": match, "name": name}))
    return out, ally_team, ally_conf


def _measure_stretch(image_bgr: np.ndarray, tokens: list, columns: list[Column]) -> float:
    """Average of two independent cues: single-digit cell shapes and the (square) agent portraits."""
    digits = estimate_stretch(tokens, max(c.y1 for c in columns))
    rows = build_rows(tokens, columns)
    starts = [r.name_x0 for r in rows if r.name]
    if len(rows) < 3 or not starts:
        return digits
    pitch = row_pitch(rows)
    bands = [(r.yc - 0.45 * pitch, r.yc + 0.45 * pitch) for r in rows]
    portraits = portrait_stretch(image_bgr, bands, float(np.median(starts)), pitch)
    return digits if portraits is None else (digits + portraits) / 2


def _read_layout(image_bgr: np.ndarray, engine: OcrEngine) -> tuple[np.ndarray, list, list[Column], float]:
    """OCR the frame; when the capture is stretched or squeezed (stretched-resolution players), undo
    it and read again so digits, names and portraits have their native shapes."""
    tokens = engine.read(image_bgr)
    columns = find_header(tokens)
    if not columns:
        raise ScoreboardNotFound("Could not find the scoreboard columns. Upload the in-game Scoreboard tab.")
    stretch = _measure_stretch(image_bgr, tokens, columns)
    if abs(stretch - 1) > STRETCH_TOLERANCE:
        image_bgr = normalize_scale(unstretch(image_bgr, stretch))
        tokens = engine.read(image_bgr)
        columns = find_header(tokens) or columns
    return image_bgr, tokens, columns, stretch


def parse_scoreboard(image_bgr: np.ndarray, engine: OcrEngine, catalog: Catalog, rosters: Rosters) -> ScoreboardResult:
    image_bgr, tokens, columns, stretch = _read_layout(image_bgr, engine)
    rows = build_rows(tokens, columns)
    if not rows:
        raise ScoreboardNotFound("Found the scoreboard header but no player rows.")

    pitch = row_pitch(rows)
    recover_cells(image_bgr, engine, rows, columns, pitch)
    recover_names(image_bgr, engine, rows, columns, pitch)
    portraits = _portraits(image_bgr, rows, pitch)
    players = [_player(image_bgr, row, columns, pitch, crop, catalog) for row, crop in zip(rows, portraits)]
    filled = fill_last_unknown([p.side for p in players])
    players = [p if conf < 0 else p.model_copy(update={"side": side, "side_confidence": conf}) for p, (side, conf) in zip(players, filled)]
    # Rows still unplaced (colour-blind palettes, hover highlights) join the team they look most like.
    tints = [row_tint(image_bgr, stat_boundary(columns), max(c.x1 for c in columns), row.yc, pitch) for row in rows]
    placed = relative_sides(tints, [(p.side, p.side_confidence) for p in players])
    players = [p.model_copy(update={"side": side, "side_confidence": conf}) for p, (side, conf) in zip(players, placed)]
    players, ally_team, ally_conf = _apply_names(players, rows, rosters)

    header = read_header(tokens, min(c.y0 for c in columns), catalog.maps, image_bgr, engine)
    warnings = [*check_score(header.ally_score.value, header.enemy_score.value), *check_rows(players), *check_kills(players)]
    if ally_team is None:
        warnings.append(ScoreboardWarning(code="TEAM_UNKNOWN", message="Could not tell which bracket team is the screenshot owner's side."))
    warnings += check_hidden_names(players)
    if abs(stretch - 1) > STRETCH_TOLERANCE:
        warnings.append(ScoreboardWarning(code="STRETCHED_CAPTURE", message=f"The screenshot was stretched ({stretch:.2f}x); it was straightened before reading."))

    height, width = image_bgr.shape[:2]
    return ScoreboardResult(
        parser_version=PARSER_VERSION,
        engine_version=engine.version,
        image_width=width,
        image_height=height,
        capture_stretch=round(stretch, 3),
        outcome=header.outcome,
        ally_score=header.ally_score,
        enemy_score=header.enemy_score,
        map=header.map,
        played_on=header.played_on,
        ally_team=OcrField(value=ally_team, confidence=ally_conf),
        columns=sorted({k for p in players for k in p.stats}),
        players=players,
        warnings=warnings,
    )
