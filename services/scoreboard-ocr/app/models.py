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
    uuid: str
    name: str
    role: str | None = None


class RosterMatch(CamelModel):
    user_id: str
    team: TeamSlot
    matched_name: str
    score: float


class PlayerRow(CamelModel):
    side: Side | None
    side_confidence: float
    name: OcrField[str]
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
    outcome: OcrField[Outcome]
    ally_score: OcrField[int]
    enemy_score: OcrField[int]
    map: OcrField[MapRef]
    ally_team: OcrField[TeamSlot]
    columns: list[str]
    players: list[PlayerRow]
    warnings: list[ScoreboardWarning] = Field(default_factory=list)


class RosterPlayer(CamelModel):
    user_id: str
    names: list[str] = Field(default_factory=list, max_length=8)


class Rosters(CamelModel):
    team1: list[RosterPlayer] = Field(default_factory=list, max_length=15)
    team2: list[RosterPlayer] = Field(default_factory=list, max_length=15)
