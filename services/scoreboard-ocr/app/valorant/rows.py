"""Group body tokens into player rows and assign values to header columns."""

from __future__ import annotations

from dataclasses import dataclass, field
from statistics import median

from app.engine import OcrToken
from app.valorant.columns import Column
from app.valorant.text import parse_number, split_kda

MAX_ROWS = 12


@dataclass
class Row:
    tokens: list[OcrToken]
    values: dict[str, tuple[float, float]] = field(default_factory=dict)
    name: str = ""
    name_conf: float = 0.0
    name_x0: float = 0.0

    @property
    def yc(self) -> float:
        return median(t.yc for t in self.tokens)

    @property
    def y0(self) -> float:
        return min(t.y0 for t in self.tokens)

    @property
    def y1(self) -> float:
        return max(t.y1 for t in self.tokens)


def group_rows(tokens: list[OcrToken], header_bottom: float) -> list[list[OcrToken]]:
    body = sorted((t for t in tokens if t.y0 > header_bottom), key=lambda t: t.yc)
    if not body:
        return []
    line_h = median(t.h for t in body)
    rows: list[list[OcrToken]] = []
    for token in body:
        if rows and abs(token.yc - median(t.yc for t in rows[-1])) <= 0.55 * line_h:
            rows[-1].append(token)
        else:
            rows.append([token])
    return [sorted(r, key=lambda t: t.x0) for r in rows]


def stat_boundary(columns: list[Column]) -> float:
    """X coordinate left of which text belongs to the player name area."""
    centers = [c.xc for c in columns]
    gaps = [b - a for a, b in zip(centers, centers[1:])] or [columns[0].x1 - columns[0].x0]
    return min(columns[0].x0, centers[0] - 0.6 * min(gaps))


def _nearest_column(token: OcrToken, columns: list[Column], tolerance: float) -> Column | None:
    best = min(columns, key=lambda c: abs(c.xc - token.xc))
    inside = best.x0 - tolerance <= token.xc <= best.x1 + tolerance
    return best if inside or abs(best.xc - token.xc) <= tolerance else None


def _assign_values(row: Row, stat_tokens: list[OcrToken], columns: list[Column]) -> None:
    centers = [c.xc for c in columns]
    gaps = [b - a for a, b in zip(centers, centers[1:])]
    tolerance = 0.45 * min(gaps) if gaps else 40.0
    kda_tokens: list[OcrToken] = []
    for token in stat_tokens:
        column = _nearest_column(token, columns, tolerance)
        if column is None:
            continue
        if column.key == "kda":
            kda_tokens.append(token)
            continue
        value, mult = parse_number(token.text)
        if value is not None:
            row.values.setdefault(column.key, (value, token.conf * mult))
    if kda_tokens:
        # "20 / 7 / 3" may come back as one token or several; read them as one string.
        text = " ".join(t.text for t in sorted(kda_tokens, key=lambda t: t.x0))
        parts = split_kda(text)
        conf = min(t.conf for t in kda_tokens)
        for key, (value, mult) in zip(("kills", "deaths", "assists"), parts or []):
            if value is not None:
                row.values.setdefault(key, (value, conf * mult))


def _assign_name(row: Row, name_tokens: list[OcrToken]) -> None:
    """The name is the text cluster nearest the stats (menus and tabs further left are ignored).
    A Premier team tag ("GTH | tr1ck") is dropped from the name."""
    if not name_tokens:
        return
    line_h = median(t.h for t in name_tokens)
    ordered = sorted(name_tokens, key=lambda t: t.x0)
    cluster = [ordered[-1]]
    for token in reversed(ordered[:-1]):
        if cluster[0].x0 - token.x1 > 1.5 * line_h:
            break
        cluster.insert(0, token)
    text = " ".join(t.text for t in cluster)
    if "|" in text:
        text = text.rsplit("|", 1)[1]
    row.name = text.strip()
    row.name_conf = min(t.conf for t in cluster)
    row.name_x0 = cluster[0].x0


def build_rows(tokens: list[OcrToken], columns: list[Column]) -> list[Row]:
    header_bottom = max(c.y1 for c in columns)
    boundary = stat_boundary(columns)
    centers = [c.xc for c in columns]
    gaps = [b - a for a, b in zip(centers, centers[1:])] or [columns[-1].x1 - columns[-1].x0]
    right_edge = columns[-1].x1 + 0.6 * min(gaps)  # side panels (friends list, party) live past this
    tokens = [t for t in tokens if t.x0 < right_edge]
    min_values = max(2, len(columns) // 2)
    rows: list[Row] = []
    for group in group_rows(tokens, header_bottom):
        row = Row(tokens=group)
        _assign_values(row, [t for t in group if t.xc >= boundary], columns)
        if len(row.values) < min_values:
            if rows:
                break  # first non-row after the table ends the scoreboard
            continue
        _assign_name(row, [t for t in group if t.xc < boundary and any(ch.isalnum() for ch in t.text)])
        rows.append(row)
        if len(rows) >= MAX_ROWS:
            break
    return rows


def row_pitch(rows: list[Row]) -> float:
    if len(rows) >= 2:
        return median(b.yc - a.yc for a, b in zip(rows, rows[1:]))
    if rows:
        return 2.2 * (rows[0].y1 - rows[0].y0)
    return 0.0
