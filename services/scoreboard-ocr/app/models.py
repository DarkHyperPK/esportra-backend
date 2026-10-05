"""Wire models for the scoreboard OCR service (camelCase JSON)."""

from __future__ import annotations

from typing import Generic, Literal, TypeVar

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel

T = TypeVar("T")

Side = Literal["ally", "enemy"]
TeamSlot = Literal["team1", "team2"]
Outcome = Literal["victory", "defeat", "draw"]

STAT_KEYS: tuple[str, ...] = (
    "acs",
    "kills",
    "deaths",
    "assists",
    "econ",
    "trades",
    "firstBloods",
    "plants",
    "defuses",
    "hsPct",
    "adr",
)


class CamelModel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, frozen=True)


class OcrField(CamelModel, Generic[T]):
    """A value read from the image plus how sure we are (0..1)."""

    value: T | None = None
    confidence: float = 0.0


class MapRef(CamelModel):
    name: str
    uuid: str | None = None
    map_url: str | None = None


class AgentRef(CamelModel):
    # valorant-api uuid; None for an agent valorant-api doesn't list yet (the name is still known).
    uuid: str | None = None
    name: str
    role: str | None = None


NameSource = Literal["riot_verified", "riot_previous", "riot_claimed", "username"]
MatchMethod = Literal["read", "elimination"]


class RosterMatch(CamelModel):
    user_id: str
    team: TeamSlot
    matched_name: str
    score: float
    # Where the matched name came from (None when the caller sent plain names).
    source: NameSource | None = None
    # "elimination": the name could not be read; this was the team's only unmatched player. Never proof.
    method: MatchMethod = "read"


class PlayerRow(CamelModel):
    side: Side | None
    side_confidence: float
    name: OcrField[str]
    # Exactly what the screenshot showed, before roster matching replaced it with the canonical name.
    name_read: str | None = None
    roster_match: RosterMatch | None = None
    agent: OcrField[AgentRef]
    stats: dict[str, OcrField[float]]


class ScoreboardWarning(CamelModel):
    code: str
    message: str
    player_index: int | None = None


class ScoreboardResult(CamelModel):
    game: Literal["valorant"] = "valorant"
    parser_version: str
    engine_version: str
    image_width: int
    image_height: int
    # Horizontal stretch measured on the capture (1.0 = native); stretched ones are straightened first.
    capture_stretch: float = 1.0
    outcome: OcrField[Outcome]
    ally_score: OcrField[int]
    enemy_score: OcrField[int]
    map: OcrField[MapRef]
    # Match date printed on the screenshot (ISO), compared with the match's date by the API.
    played_on: OcrField[str] = Field(default_factory=OcrField[str])
    ally_team: OcrField[TeamSlot]
    columns: list[str]
    players: list[PlayerRow]
    warnings: list[ScoreboardWarning] = Field(default_factory=list)


class RosterName(CamelModel):
    name: str = Field(max_length=64)
    source: NameSource


class RosterPlayer(CamelModel):
    user_id: str
    # Plain names (older API versions and the debug bench); treated as Riot game names.
    names: list[str] = Field(default_factory=list, max_length=8)
    candidates: list[RosterName] = Field(default_factory=list, max_length=12)


class Rosters(CamelModel):
    team1: list[RosterPlayer] = Field(default_factory=list, max_length=15)
    team2: list[RosterPlayer] = Field(default_factory=list, max_length=15)
