"""Accuracy on real Valorant client screenshots (tests/fixtures/real, hand-transcribed JSON).

Each JSON lists the expected score, outcome, map and every row: side, name, the stat columns the
screenshot shows, and (for the newer fixtures) the agent and the name's script. Rosters are built from
the expected names, as the API would send them.

- Names in a script the installed recognisers can't read (Arabic/Hangul without the optional models)
  may stay unread: they must then be matched by elimination or left unmatched, never matched wrongly.
- Truncated names ("truncated": true) must contain the visible fragment.
- Cells the game itself draws a long name over ("occluded") must be empty or right, never guessed.
- Agents are checked leave-one-screenshot-out: the screenshot's own crops are removed from the bank.
"""

import json
from dataclasses import replace
from pathlib import Path

import pytest

from app.image_io import decode_image
from app.models import RosterPlayer, Rosters
from app.valorant.catalog import fallback_catalog
from app.valorant.pipeline import parse_scoreboard
from app.valorant.text import match_key

pytest.importorskip("rapidocr")

FIXTURES = Path(__file__).parent / "fixtures" / "real"
CASES = sorted(p for p in FIXTURES.iterdir() if p.suffix in {".png", ".jpg", ".webp"})
STATS = ("kills", "deaths", "assists", "firstBloods", "plants", "defuses", "acs", "trades", "econ")
TOTALS: dict[str, list[int]] = {}


@pytest.fixture(scope="module")
def engine():
    from app.engine import RapidOcrEngine

    return RapidOcrEngine()


def _rosters(rows: list[dict]) -> Rosters:
    def team(side: str) -> list[RosterPlayer]:
        return [RosterPlayer(user_id=r["name"], names=[r["name"]]) for r in rows if r["side"] == side and r["name"]]

    return Rosters(team1=team("ally"), team2=team("enemy"))


def _tally(key: str, ok: int, total: int) -> None:
    got = TOTALS.setdefault(key, [0, 0])
    got[0] += ok
    got[1] += total


def _readable(row: dict, scripts: tuple[str, ...]) -> bool:
    script = row.get("script", "latin")
    return script in ("latin", "cjk") or script in scripts


@pytest.mark.parametrize("image_path", CASES, ids=[p.stem for p in CASES])
def test_real_screenshot(engine, image_path: Path):
    expected = json.loads(image_path.with_suffix(".json").read_text(encoding="utf-8"))
    rows = expected["rows"]
    catalog = fallback_catalog()
    catalog = replace(catalog, bank=catalog.bank.without_source(image_path.stem))
    result = parse_scoreboard(decode_image(image_path.read_bytes()), engine, catalog, _rosters(rows))

    assert result.outcome.value == expected["outcome"]
    assert (result.ally_score.value, result.enemy_score.value) == (expected["ally"], expected["enemy"])
    assert result.map.value is not None and result.map.value.name == expected["map"]
    assert result.ally_team.value == "team1"
    assert "STRETCHED_CAPTURE" not in [w.code for w in result.warnings]  # native captures are read as they are
    assert len(result.players) == len(rows)
    assert [p.side for p in result.players] == [r["side"] for r in rows]

    for player, row in zip(result.players, rows):
        for key in row.get("occluded", []):
            # The game draws the name over this number: it must be left empty or read right, never guessed.
            assert player.stats[key].value in (None, row[key])
    cells = [
        (p.stats[k].value if k in p.stats else None, r[k])
        for p, r in zip(result.players, rows)
        for k in STATS
        if k in r and k not in r.get("occluded", [])
    ]
    correct = sum(got == want for got, want in cells)
    _tally("numeric cells", correct, len(cells))
    print(f"{image_path.stem}: {correct}/{len(cells)} numeric cells")
    assert correct / len(cells) >= 0.99

    for player, row in zip(result.players, rows):
        name, match = row["name"], player.roster_match
        if not name:
            continue
        if not _readable(row, engine.scripts):
            assert match is None or (match.user_id == name and match.method == "elimination")
            _tally("unreadable-script names matched by elimination", int(match is not None), 1)
            continue
        assert match is not None and match.user_id == name, f"{name!r} matched {match}"
        shown = match_key(player.name.value or "")
        if row.get("truncated"):
            assert match_key(name) in shown or shown in match_key(name)
        else:
            assert shown == match_key(name), f"{player.name.value!r} != {name!r}"
        _tally("names", 1, 1)

    with_agents = [(p, r) for p, r in zip(result.players, rows) if r.get("agent")]
    if with_agents:
        hits = sum(p.agent.value is not None and p.agent.value.name == r["agent"] for p, r in with_agents)
        _tally("agents (leave-one-out)", hits, len(with_agents))
        print(f"{image_path.stem}: agents {hits}/{len(with_agents)}")


def test_zz_report():
    """Printed accuracy summary (run with -s)."""
    for key, (ok, total) in TOTALS.items():
        print(f"{key}: {ok}/{total} = {ok / max(total, 1):.1%}")
    agents = TOTALS.get("agents (leave-one-out)")
    if agents:
        assert agents[0] / agents[1] >= 0.95
