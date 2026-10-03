"""Match OCR'd player names to the two rosters and work out which team is the ally side."""

from __future__ import annotations

from dataclasses import dataclass

from rapidfuzz import fuzz

from app.models import Rosters, RosterMatch, Side, TeamSlot
from app.valorant.text import normalize_name

MIN_SCORE = 70.0


@dataclass(frozen=True)
class _Candidate:
    user_id: str
    team: TeamSlot
    name: str
    norm: str


def _candidates(rosters: Rosters) -> list[_Candidate]:
    out: list[_Candidate] = []
    for team, players in (("team1", rosters.team1), ("team2", rosters.team2)):
        for player in players:
            for name in player.names:
                norm = normalize_name(name)
                if norm:
                    out.append(_Candidate(player.user_id, team, name, norm))  # type: ignore[arg-type]
    return out


def similarity(ocr_norm: str, roster_norm: str) -> float:
    if not ocr_norm or not roster_norm:
        return 0.0
    score = fuzz.ratio(ocr_norm, roster_norm)
    if min(len(ocr_norm), len(roster_norm)) >= 4:
        score = max(score, 0.9 * fuzz.partial_ratio(ocr_norm, roster_norm))
    return float(score)


def match_names(names: list[str], rosters: Rosters) -> list[RosterMatch | None]:
    """Greedy one-to-one assignment of rows to roster players, best pairs first."""
    candidates = _candidates(rosters)
    pairs: list[tuple[float, int, _Candidate]] = []
    for index, name in enumerate(names):
        norm = normalize_name(name)
        for cand in candidates:
            score = similarity(norm, cand.norm)
            if score >= MIN_SCORE:
                pairs.append((score, index, cand))
    pairs.sort(key=lambda p: p[0], reverse=True)

    result: list[RosterMatch | None] = [None] * len(names)
    taken_users: set[str] = set()
    for score, index, cand in pairs:
        if result[index] is not None or cand.user_id in taken_users:
            continue
        taken_users.add(cand.user_id)
        result[index] = RosterMatch(user_id=cand.user_id, team=cand.team, matched_name=cand.name, score=round(score / 100, 3))
    return result


def resolve_ally_team(sides: list[Side | None], matches: list[RosterMatch | None]) -> tuple[TeamSlot | None, float]:
    """Vote: an ally row matched to team1 (or an enemy row matched to team2) says team1 is the ally."""
    votes = {"team1": 0.0, "team2": 0.0}
    for side, match in zip(sides, matches):
        if side is None or match is None:
            continue
        other = "team2" if match.team == "team1" else "team1"
        votes[match.team if side == "ally" else other] += match.score
    total = votes["team1"] + votes["team2"]
    if total == 0:
        return None, 0.0
    winner: TeamSlot = "team1" if votes["team1"] >= votes["team2"] else "team2"
    margin = abs(votes["team1"] - votes["team2"]) / total
    return winner, round(margin, 3)
