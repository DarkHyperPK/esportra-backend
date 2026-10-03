"""End-to-end: real OCR engine on a rendered scoreboard with known values."""

import pytest

from app.models import MapRef, RosterPlayer, Rosters
from app.valorant.catalog import Catalog
from app.valorant.pipeline import ScoreboardNotFound, parse_scoreboard
from tests.synthetic import SAMPLE, STAT_FIELDS, make_agent_templates, render

pytest.importorskip("rapidocr")


@pytest.fixture(scope="module")
def engine():
    from app.engine import RapidOcrEngine

    return RapidOcrEngine()


@pytest.fixture(scope="module")
def templates():
    return make_agent_templates()


@pytest.fixture(scope="module")
def result(engine, templates):
    image = render(SAMPLE, templates, 13, 1, "VICTORY", "ASCENT")
    catalog = Catalog(agents=templates, maps=[MapRef(name="Ascent", uuid="m1", map_url="/Game/Maps/Ascent/Ascent")])
    rosters = Rosters(
        team1=[RosterPlayer(user_id=f"t1-{i}", names=[p.name]) for i, p in enumerate(SAMPLE) if p.ally],
        team2=[RosterPlayer(user_id=f"t2-{i}", names=[p.name]) for i, p in enumerate(SAMPLE) if not p.ally],
    )
    return parse_scoreboard(image, engine, catalog, rosters)


def test_summary(result):
    assert result.outcome.value == "victory"
    assert (result.ally_score.value, result.enemy_score.value) == (13, 1)
    assert result.map.value is not None and result.map.value.map_url == "/Game/Maps/Ascent/Ascent"
    assert result.ally_team.value == "team1"


def test_rows_values_sides_agents(result):
    assert len(result.players) == 10
    total = correct = 0
    for row, exp in zip(result.players, SAMPLE):
        assert row.side == ("ally" if exp.ally else "enemy")
        assert row.roster_match is not None and row.roster_match.matched_name == exp.name
        assert row.agent.value is not None and row.agent.value.uuid == f"agent-{exp.agent}"
        for key, attr in STAT_FIELDS:
            total += 1
            correct += int(row.stats[key].value == getattr(exp, attr))
    assert correct / total >= 0.98, f"{correct}/{total} numeric cells correct"
    assert "acs" not in result.columns  # the real scoreboard has no ACS column


def test_ignores_menu_duration_and_sidebar(result):
    names = {p.name.value for p in result.players}
    assert not names & {"HEAD TO HEAD", "TIMELINE", "SCOREBOARD", "e"}
    assert (result.ally_score.value, result.enemy_score.value) != (34, 57)


def test_no_blocking_warnings(result):
    codes = {w.code for w in result.warnings}
    assert not codes & {"ROW_COUNT", "SIDE_UNKNOWN", "TEAM_UNKNOWN", "KILLS_DEATHS_MISMATCH", "SCORE_MISSING"}


def test_rejects_images_without_a_scoreboard(engine, templates):
    import numpy as np

    blank = np.full((1080, 1920, 3), 30, dtype=np.uint8)
    with pytest.raises(ScoreboardNotFound):
        parse_scoreboard(blank, engine, Catalog(agents=templates, maps=[]), Rosters())


def test_engine_detection_survives_recognition_only_calls(engine, templates):
    """Regression: RapidOCR used to keep use_det=False after a recognize() call."""
    image = render(SAMPLE, templates, 13, 1, "VICTORY", "ASCENT")
    engine.recognize(image[50:130, 30:140])
    assert len(engine.read(image)) > 50
