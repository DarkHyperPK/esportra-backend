"""Sanity checks. They never block; they tell the captain where to look."""

from __future__ import annotations

from app.models import OcrField, PlayerRow, ScoreboardWarning

LOW_CONFIDENCE = 0.6


def _value(field: OcrField[float] | None) -> float | None:
    return field.value if field is not None else None


def check_rows(players: list[PlayerRow]) -> list[ScoreboardWarning]:
    allies = sum(1 for p in players if p.side == "ally")
    enemies = sum(1 for p in players if p.side == "enemy")
    out: list[ScoreboardWarning] = []
    if len(players) != 10 or allies != 5 or enemies != 5:
        out.append(ScoreboardWarning(code="ROW_COUNT", message=f"Expected 5 players per team, read {allies} ally and {enemies} enemy rows out of {len(players)}."))
    for index, player in enumerate(players):
        if player.side is None:
            out.append(ScoreboardWarning(code="SIDE_UNKNOWN", message="Could not tell which team this row belongs to.", player_index=index))
        if player.roster_match is None:
            out.append(ScoreboardWarning(code="NAME_UNMATCHED", message="Name does not match anyone on either roster.", player_index=index))
        if player.agent.value is None or player.agent.confidence < LOW_CONFIDENCE:
            out.append(ScoreboardWarning(code="AGENT_UNCERTAIN", message="Agent could not be identified with confidence.", player_index=index))
        acs = _value(player.stats.get("acs"))
        if acs is not None and not 0 <= acs <= 1000:
            out.append(ScoreboardWarning(code="ACS_OUT_OF_RANGE", message=f"ACS {acs:g} is outside 0-1000.", player_index=index))
        low = [k for k, f in player.stats.items() if f.value is not None and f.confidence < LOW_CONFIDENCE]
        if low:
            out.append(ScoreboardWarning(code="LOW_CONFIDENCE", message=f"Check these values: {', '.join(sorted(low))}.", player_index=index))
    return out


def _team_total(players: list[PlayerRow], side: str, key: str) -> float | None:
    values = [_value(p.stats.get(key)) for p in players if p.side == side]
    if not values or any(v is None for v in values):
        return None
    return float(sum(v for v in values if v is not None))


def check_kills(players: list[PlayerRow]) -> list[ScoreboardWarning]:
    out: list[ScoreboardWarning] = []
    for side, other in (("ally", "enemy"), ("enemy", "ally")):
        kills, deaths = _team_total(players, side, "kills"), _team_total(players, other, "deaths")
        if kills is not None and deaths is not None and abs(kills - deaths) > 3:
            out.append(ScoreboardWarning(code="KILLS_DEATHS_MISMATCH", message=f"{side.title()} kills ({kills:g}) do not line up with {other} deaths ({deaths:g})."))
    return out


def check_score(ally: int | None, enemy: int | None) -> list[ScoreboardWarning]:
    if ally is None or enemy is None:
        return [ScoreboardWarning(code="SCORE_MISSING", message="Round score could not be read.")]
    high, low = max(ally, enemy), min(ally, enemy)
    regulation = high == 13 and low <= 11
    overtime = high >= 14 and high - low == 2
    if regulation or overtime:
        return []
    return [ScoreboardWarning(code="SCORE_UNUSUAL", message=f"{ally}-{enemy} is not a normal Valorant final score.")]
