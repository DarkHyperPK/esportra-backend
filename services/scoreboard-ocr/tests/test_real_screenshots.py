"""Accuracy on real Valorant client screenshots (tests/fixtures/real, hand-transcribed JSON).

Each JSON lists the expected score, outcome, map and every row (side, name, K/D/A, first bloods,
plants, defuses). A row with "name": null is a name the bundled model cannot read (Hangul); its
stats are still checked. Rosters are built from the expected names, as the API would send them.
"""

import json
from pathlib import Path

import pytest

from app.image_io import decode_image
from app.models import RosterPlayer, Rosters
from app.valorant.catalog import fallback_catalog
from app.valorant.pipeline import parse_scoreboard

pytest.importorskip("rapidocr")

FIXTURES = Path(__file__).parent / "fixtures" / "real"
CASES = sorted(p for p in FIXTURES.iterdir() if p.suffix in {".png", ".jpg", ".webp"})
STATS = ("kills", "deaths", "assists", "firstBloods", "plants", "defuses")


@pytest.fixture(scope="module")
def engine():
    from app.engine import RapidOcrEngine

    return RapidOcrEngine()


def _rosters(rows: list[dict]) -> Rosters:
    def team(side: str) -> list[RosterPlayer]:
        return [RosterPlayer(user_id=r["name"], names=[r["name"]]) for r in rows if r["side"] == side and r["name"]]

    return Rosters(team1=team("ally"), team2=team("enemy"))


@pytest.mark.parametrize("image_path", CASES, ids=[p.stem for p in CASES])
def test_real_screenshot(engine, image_path: Path):
    expected = json.loads(image_path.with_suffix(".json").read_text(encoding="utf-8"))
    rows = expected["rows"]
    result = parse_scoreboard(decode_image(image_path.read_bytes()), engine, fallback_catalog(), _rosters(rows))

    assert result.outcome.value == expected["outcome"]
    assert (result.ally_score.value, result.enemy_score.value) == (expected["ally"], expected["enemy"])
    assert result.map.value is not None and result.map.value.name == expected["map"]
    assert result.ally_team.value == "team1"
    assert len(result.players) == len(rows)
    assert [p.side for p in result.players] == [r["side"] for r in rows]

    cells = [(p.stats[key].value, r[key]) for p, r in zip(result.players, rows) for key in STATS]
    accuracy = sum(got == want for got, want in cells) / len(cells)
    print(f"{image_path.stem}: {accuracy:.1%} of {len(cells)} numeric cells")
    assert accuracy >= 0.98

    for player, row in zip(result.players, rows):
        if row["name"]:
            assert player.roster_match is not None and player.roster_match.matched_name == row["name"]
            assert (player.name.value or "").casefold() == row["name"].casefold()
