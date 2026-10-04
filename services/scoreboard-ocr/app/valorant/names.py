"""Match OCR'd player names to the two rosters and work out which team is the ally side.

Roster names arrive with where they came from (a verified Riot ID, a previous Riot name, a self-typed
Riot tag or an Esportra username). Matching ignores spacing, case, punctuation and OCR look-alikes,
works in any script, and tolerates names the client truncated. When a row's name could not be read at
all, the last unmatched player of that team is assigned by elimination, which is flagged as such and is
never treated as proof of identity.
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass

from rapidfuzz import fuzz

from app.models import NameSource, Rosters, RosterMatch, Side, TeamSlot
from app.valorant.text import match_key

MIN_SCORE = 70.0
# Riot game names: show the roster spelling once matched. Usernames can differ from the in-game name.
_CANONICAL_SOURCES = {None, "riot_verified", "riot_previous", "riot_claimed"}
_SOURCE_RANK = {"riot_verified": 0, None: 1, "riot_previous": 2, "riot_claimed": 3, "username": 4}


@dataclass(frozen=True)
class _Candidate:
    user_id: str
    team: TeamSlot
    name: str
    key: str
    source: NameSource | None


def _candidates(rosters: Rosters) -> list[_Candidate]:
    out: list[_Candidate] = []
    for team, players in (("team1", rosters.team1), ("team2", rosters.team2)):
        for player in players:
            named = [(c.name, c.source) for c in player.candidates] + [(n, None) for n in player.names]
            for name, source in named:
                key = match_key(name)
                if key:
                    out.append(_Candidate(player.user_id, team, name.strip(), key, source))  # type: ignore[arg-type]
    return out


def similarity(read_key: str, roster_key: str) -> float:
    if not read_key or not roster_key:
        return 0.0
    score = fuzz.ratio(read_key, roster_key)
    if min(len(read_key), len(roster_key)) >= 4:
        # Long names are cut off by the client ("ormBreake" for "StormBreaker").
        score = max(score, 0.9 * fuzz.partial_ratio(read_key, roster_key))
    return float(score)


# A Premier tag whose "|" separator was read as l/I: "ARClaayan" -> tag "ARC", name "aayan".
_GLUED_TAG = re.compile(r"^[A-Z0-9]{2,5}[lI|](?P<name>.{2,})$")


def _is_rtl(text: str) -> bool:
    return any(unicodedata.bidirectional(ch) in ("R", "AL") for ch in text)


def name_variants(name: str) -> list[str]:
    """The name as read, plus the name without a glued team tag when one looks present, plus the
    reversed text for right-to-left scripts (recognisers can return Arabic in visual order)."""
    match = _GLUED_TAG.match(name.strip())
    variants = [name, match.group("name")] if match else [name]
    return variants + [v[::-1] for v in variants if _is_rtl(v)]


def _shown(cand: _Candidate, variant: str) -> str:
    return cand.name if cand.source in _CANONICAL_SOURCES else variant


def match_names(names: list[str], rosters: Rosters) -> list[tuple[RosterMatch | None, str]]:
    """Greedy one-to-one assignment of rows to roster players, best pairs first.
    Returns the match and the name to show (the Riot spelling once matched, else the read)."""
    candidates = _candidates(rosters)
    pairs: list[tuple[float, int, _Candidate, str]] = []
    for index, name in enumerate(names):
        for variant in name_variants(name):
            key = match_key(variant)
            for cand in candidates:
                score = similarity(key, cand.key)
                if score >= MIN_SCORE:
                    pairs.append((score, index, cand, variant))
    # Best score first; on ties prefer verified Riot names over other sources.
    pairs.sort(key=lambda p: (-p[0], _SOURCE_RANK[p[2].source]))

    result: list[tuple[RosterMatch | None, str]] = [(None, name) for name in names]
    taken_users: set[str] = set()
    for score, index, cand, variant in pairs:
        if result[index][0] is not None or cand.user_id in taken_users:
            continue
        taken_users.add(cand.user_id)
        match = RosterMatch(
            user_id=cand.user_id, team=cand.team, matched_name=cand.name, score=round(score / 100, 3), source=cand.source
        )
        result[index] = (match, _shown(cand, variant))
    return result


def _preferred_name(player) -> tuple[str, NameSource | None]:  # noqa: ANN001 - RosterPlayer
    for source in ("riot_verified", "riot_claimed", "username"):
        for cand in player.candidates:
            if cand.source == source:
                return cand.name, cand.source
    return (player.names[0], None) if player.names else (player.candidates[0].name, player.candidates[0].source)


def match_by_elimination(
    names: list[str],
    sides: list[Side | None],
    matched: list[tuple[RosterMatch | None, str]],
    rosters: Rosters,
    ally_team: TeamSlot | None,
) -> list[tuple[RosterMatch | None, str]]:
    """A row whose name couldn't be read gets its team's last unmatched player, when that row is the only
    unmatched one on its side and exactly one roster player is left. Leftover substitutes block it."""
    if ally_team is None:
        return matched
    enemy_team: TeamSlot = "team2" if ally_team == "team1" else "team1"
    taken = {m.user_id for m, _ in matched if m is not None}
    out = list(matched)
    for side, team in (("ally", ally_team), ("enemy", enemy_team)):
        unmatched = [i for i, s in enumerate(sides) if s == side and out[i][0] is None]
        players = [p for p in getattr(rosters, team) if p.user_id not in taken and (p.names or p.candidates)]
        if len(unmatched) != 1 or names[unmatched[0]].strip() or len(players) != 1:
            continue
        name, source = _preferred_name(players[0])
        match = RosterMatch(user_id=players[0].user_id, team=team, matched_name=name, score=0.0, source=source, method="elimination")
        out[unmatched[0]] = (match, name)
    return out


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
