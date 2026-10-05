"""Rules added with the 2026 client layouts, the player-screenshot dataset and roster verification."""

from pathlib import Path

import cv2
import httpx
import numpy as np
import pytest

from app.engine import OcrToken
from app.image_io import trim_black_bars
from app.models import AgentRef, OcrField, PlayerRow, RosterName, RosterPlayer, Rosters
from app.valorant import catalog as catalog_module
from app.valorant.agent_bank import AgentBank, bundled_bank
from app.valorant.catalog import Catalog, fallback_catalog, refresh_if_changed
from app.valorant.checks import check_hidden_names
from app.valorant.columns import find_header
from app.valorant.header import is_early_end, read_played_on
from app.valorant.layout import estimate_stretch, table_left
from app.valorant.names import match_by_elimination, match_names, name_variants
from app.valorant.rows import build_rows
from app.valorant.sides import relative_sides
from app.valorant.text import match_key, split_kda


def tok(text: str, x: float, y: float, w: float = 40, h: float = 20, conf: float = 0.99) -> OcrToken:
    return OcrToken(text, conf, x - w / 2, y - h / 2, x + w / 2, y + h / 2)


def test_performance_score_and_trades_columns():
    header = [tok("PERFORMANCE SCORE", 600, 100, w=160), tok("KDA", 760, 100), tok("TRADES", 900, 100, w=60), tok("PLANTS", 1050, 100, w=60)]
    assert [c.key for c in find_header(header)] == ["acs", "kda", "trades", "plants"]


def test_match_key_ignores_spacing_case_lookalikes_and_keeps_any_script():
    assert match_key("L 1 t e") == match_key("L1te")
    assert match_key("ZyWoO") == match_key("ZyWo0")
    assert match_key("كبده وصدوقها") == "كبدهوصدوقها"
    assert match_key("Js木") == "j5木"


def test_split_kda_with_a_lost_separator_is_low_confidence():
    """Real case: "14/ 14 19" was 14 / 14 / 9 with the second slash read as "1"."""
    parts = split_kda("14/ 14 19")
    assert parts is not None and all(conf <= 0.5 for _, conf in parts)


def test_rank_badge_glyph_and_menu_text_never_become_names():
    header = [tok("ACS", 800, 100), tok("K", 900, 100), tok("D", 1000, 100), tok("A", 1100, 100)]
    body = []
    for i in range(5):
        y = 160 + i * 48
        body += [tok("300", 800, y), tok("10", 900, y), tok("5", 1000, y), tok("3", 1100, y)]
        body += [tok(f"Player{i}", 520, y, w=90)] if i else [tok("PERFORMANCE", 110, y, w=130)]  # unreadable name
    body.insert(0, tok("口", 430, 208, w=18))  # rank badge read as a glyph left of "Player1"
    rows = build_rows(header + body, find_header(header))
    assert rows[0].name == "" and rows[1].name == "Player1"


def test_glued_score_digits_are_stripped_from_a_long_name():
    header = [tok("ACS", 600, 100), tok("K", 760, 100), tok("D", 860, 100), tok("A", 960, 100)]
    rows = build_rows(
        header + [tok("Rawzu from temu394", 520, 160, w=200), tok("20", 760, 160), tok("8", 860, 160), tok("4", 960, 160)],
        find_header(header),
    )
    assert rows[0].name == "Rawzu from temu"


def _rosters() -> Rosters:
    return Rosters(
        team1=[
            RosterPlayer(user_id="u1", candidates=[RosterName(name="L1te", source="riot_verified"), RosterName(name="lite_es", source="username")]),
            RosterPlayer(user_id="u2", candidates=[RosterName(name="سعد", source="riot_verified")]),
        ],
        team2=[RosterPlayer(user_id="u3", candidates=[RosterName(name="Kaidn", source="riot_claimed")])],
    )


def test_matched_rows_show_the_riot_spelling_and_carry_the_source():
    named = match_names(["L 1 t e", "Kaidn"], _rosters())
    (m1, shown1), (m2, _) = named
    assert m1 is not None and m1.user_id == "u1" and m1.source == "riot_verified" and shown1 == "L1te"
    assert m2 is not None and m2.source == "riot_claimed" and m2.method == "read"


def test_unread_name_is_matched_by_elimination_and_flagged():
    names = ["L1te", "", "Kaidn"]
    sides = ["ally", "ally", "enemy"]
    matched = match_by_elimination(names, sides, match_names(names, _rosters()), _rosters(), "team1")
    assert matched[1][0] is not None and matched[1][0].user_id == "u2" and matched[1][0].method == "elimination"
    assert matched[1][0].score == 0.0


def test_elimination_never_guesses_with_leftover_substitutes():
    rosters = _rosters().model_copy(update={"team1": [*_rosters().team1, RosterPlayer(user_id="sub", names=["Bench"])]})
    names, sides = ["L1te", "", "Kaidn"], ["ally", "ally", "enemy"]
    matched = match_by_elimination(names, sides, match_names(names, rosters), rosters, "team1")
    assert matched[1][0] is None


def test_arabic_names_also_match_in_visual_order():
    assert "دعس" in name_variants("سعد")


def test_estimate_stretch_from_digit_cells():
    native = [tok("7", 100 + i * 30, 300, w=16.2, h=20) for i in range(12)]
    wide = [tok("7", 100 + i * 30, 300, w=22.9, h=20) for i in range(12)]
    assert abs(estimate_stretch(native, 200) - 1.0) < 0.05
    assert estimate_stretch(wide, 200) > 1.3


def test_table_left_is_the_first_edge_shared_by_every_row():
    img = np.full((400, 600, 3), 30, np.uint8)
    rng = np.random.default_rng(1)
    bands = []
    for i in range(5):
        y0 = 40 + i * 60
        img[y0 : y0 + 50, 200:] = (120, 110, 40)
        img[y0 : y0 + 50, 200:250] = rng.integers(0, 255, (50, 50, 3))  # portrait
        if i < 2:
            img[y0 + 10 : y0 + 20, 20 : 20 + 30 * (i + 1)] = 230  # the side menu covers only a few rows
        bands.append((y0 + 5, y0 + 45))
    assert abs(table_left(img, bands, name_x0=320, pitch=60) - 200) <= 2


def test_surrender_scores_and_match_date():
    assert is_early_end(9, 4, "victory") and not is_early_end(13, 4, "victory") and not is_early_end(4, 9, "victory")
    assert read_played_on([tok("SEP 26. 2026", 0, 0)]).value == "2026-09-26"
    assert read_played_on([tok("MAP - LOTUS", 0, 0)]).value is None


def test_trim_black_bars_keeps_only_the_game_frame():
    frame = np.full((1080, 1440, 3), 60, np.uint8)
    boxed = cv2.copyMakeBorder(frame, 0, 0, 240, 240, cv2.BORDER_CONSTANT, value=(0, 0, 0))
    assert trim_black_bars(boxed).shape == frame.shape
    assert trim_black_bars(frame).shape == frame.shape


def _row(read: str, agent: str | None) -> PlayerRow:
    return PlayerRow(
        side="ally", side_confidence=1, name=OcrField[str](value=read, confidence=1), name_read=read,
        agent=OcrField[AgentRef](value=AgentRef(name=agent) if agent else None, confidence=1), stats={},
    )


def test_hidden_names_warning_only_when_most_rows_show_agent_names():
    assert check_hidden_names([_row("Jett", "Jett")] + [_row(f"p{i}", "Sova") for i in range(9)]) == []
    hidden = [_row(a, a) for a in ("Jett", "Sova", "Omen", "Sage", "Reyna", "Skye")] + [_row("x", "Neon")] * 4
    assert [w.code for w in check_hidden_names(hidden)] == ["NAMES_HIDDEN"]


def test_blue_grey_owner_row_is_ally():
    from app.valorant.sides import classify_band

    band = np.full((20, 200, 3), (87, 79, 69), np.uint8)  # BGR of the 2026 owner row (hue ~216, sat ~52)
    assert classify_band(band)[0] == "ally"


def test_unplaced_rows_join_the_closest_team_colour():
    teal, maroon = np.array([0.0, 0.6, 0.3]), np.array([0.3, -0.2, 0.3])
    tints = [teal, teal + 0.01, maroon, maroon - 0.01, teal + 0.05, maroon + 0.05]
    sides = [("ally", 1.0), ("ally", 1.0), ("enemy", 1.0), ("enemy", 1.0), (None, 0.0), (None, 0.0)]
    assert [s for s, _ in relative_sides(tints, sides)] == ["ally", "ally", "enemy", "enemy", "ally", "enemy"]


def test_agent_bank_learns_from_crops_and_supports_leave_one_out():
    bank = bundled_bank()
    assert {"Miks", "Vyse", "Waylay", "Clove", "KAY/O"} <= bank.agents
    crop = cv2.imread(str(next((Path(__file__).parents[1] / "app/valorant/assets/agent_bank/Vyse").glob("*.png"))))
    assert bank.identify(crop).name == "Vyse"
    small = AgentBank.from_crops([("Jett", "a__0", crop)])
    assert small.without_source("a").names == []


def test_agents_valorant_api_does_not_list_yet_keep_their_name():
    from app.valorant.agents import resolve_ref

    assert resolve_ref("Miks", fallback_catalog()) == AgentRef(uuid=None, name="Miks")


def test_catalog_refresh_only_when_the_patch_changes(monkeypatch, tmp_path):
    current = Catalog(agents=[], maps=[], version="release-12.0")
    monkeypatch.setattr(catalog_module, "fetch_version", lambda: "release-12.0")
    assert refresh_if_changed(tmp_path, current.__class__(**{**current.__dict__, "agents": [object()]})) is None  # type: ignore[list-item]
    monkeypatch.setattr(catalog_module, "fetch_version", lambda: None)
    assert refresh_if_changed(tmp_path, current) is None  # offline keeps the current catalog

    def fail(*_args, **_kwargs):
        raise httpx.ConnectError("down")

    monkeypatch.setattr(catalog_module, "fetch_version", lambda: "release-12.1")
    monkeypatch.setattr(catalog_module, "_download", fail)
    assert refresh_if_changed(tmp_path, current) is None


def _bare_engine():
    import threading

    from app import engine as engine_module

    engine = engine_module.RapidOcrEngine.__new__(engine_module.RapidOcrEngine)
    engine._lock, engine._script_engines, engine.missing = threading.Lock(), {}, {}
    return engine


@pytest.mark.parametrize("script", ["arabic", "korean"])
def test_missing_script_model_is_reported_then_hot_added(monkeypatch, tmp_path, script):
    rapidocr = pytest.importorskip("rapidocr")
    from app import engine as engine_module

    monkeypatch.setattr(engine_module, "models_dir", lambda: tmp_path)
    monkeypatch.setattr(rapidocr, "RapidOCR", lambda **_: object())
    engine = _bare_engine()

    assert not engine.add_script(script)
    assert engine.scripts == () and engine.missing == {script: "model not installed"}

    (tmp_path / engine_module.SCRIPT_MODELS[script]).write_bytes(b"onnx")
    assert engine.add_script(script)
    assert engine.scripts == (script,) and engine.missing == {}


def test_installer_retries_until_the_download_works():
    import asyncio

    from app.script_installer import install_missing_scripts

    class Engine:
        def __init__(self):
            self.missing = {"arabic": "model not installed", "korean": "model not installed"}
            self.added: list[str] = []

        def add_script(self, script):
            self.added.append(script)
            self.missing.pop(script, None)
            return True

    answers = {"arabic": ["download failed: timeout", None], "korean": [None]}
    seen: list[str] = []

    def download(script):
        seen.append(script)
        return answers[script].pop(0)

    engine = Engine()
    asyncio.run(asyncio.wait_for(install_missing_scripts(engine, download, delays=(0, 0), every=0), timeout=5))
    assert engine.missing == {} and sorted(engine.added) == ["arabic", "korean"]
    assert seen.count("arabic") == 2 and seen.count("korean") == 1


@pytest.mark.parametrize(
    ("text", "expected"),
    [("23／26/6", [23, 26, 6]), ("２３ / ２６ / ６", [23, 26, 6]), ("19 ∕ 18 ⁄ 7", [19, 18, 7]), ("4｜5｜6", [4, 5, 6])],
)
def test_kda_accepts_slash_and_digit_lookalikes(text, expected):
    from app.valorant.text import split_kda

    parts = split_kda(text)
    assert parts is not None and [v for v, _ in parts] == expected
    assert all(c == 1.0 for _, c in parts)


def test_full_width_digits_parse_as_numbers():
    from app.valorant.text import parse_number

    assert parse_number("２５３") == (253.0, 1.0)
