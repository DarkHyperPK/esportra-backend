from app.engine import OcrToken
from app.models import OcrField, PlayerRow, RosterMatch, RosterPlayer, Rosters
from app.valorant.checks import check_kills, check_score
from app.valorant.columns import find_header, match_alias
from app.valorant.header import read_score
from app.valorant.names import match_names, resolve_ally_team
from app.valorant.rows import build_rows
from app.valorant.text import normalize_name, parse_number, split_kda


def tok(text: str, x: float, y: float, w: float = 40, h: float = 20, conf: float = 0.99) -> OcrToken:
    return OcrToken(text, conf, x - w / 2, y - h / 2, x + w / 2, y + h / 2)


def test_parse_number_handles_lookalikes_with_lower_confidence():
    assert parse_number("432") == (432.0, 1.0)
    assert parse_number("33%") == (33.0, 1.0)
    assert parse_number("1O") == (10.0, 0.75)
    assert parse_number("KOOLTKK") == (None, 0.0)


def test_split_kda():
    assert split_kda("26 / 6 / 4") == [(26.0, 1.0), (6.0, 1.0), (4.0, 1.0)]
    assert split_kda("26/6") is None


def test_normalize_name_maps_stylized_cyrillic():
    assert normalize_name("MЯNOЪODY") == "mrnobody"
    assert normalize_name("Kooltkk#EU1") == "kooltkk"


def test_match_alias_exact_for_short_and_fuzzy_for_long():
    assert match_alias("K")[0] == "kills"
    assert match_alias("AVG COMBAT SC0RE")[0] == "acs"
    assert match_alias("X")[0] is None


def test_find_header_merges_stacked_words():
    tokens = [tok("FIRST", 500, 100), tok("BLOODS", 500, 122), tok("ACS", 200, 110), tok("K", 300, 110), tok("D", 380, 110)]
    keys = [c.key for c in find_header(tokens)]
    assert keys == ["acs", "kills", "deaths", "firstBloods"]


def test_build_rows_assigns_values_and_name():
    header = [tok("ACS", 400, 100), tok("K", 500, 100), tok("D", 600, 100), tok("A", 700, 100)]
    body = [
        tok("KOOLTKK", 150, 160, w=120), tok("432", 400, 160), tok("26", 500, 160), tok("6", 600, 160), tok("4", 700, 160),
        tok("DEX", 150, 220, w=60), tok("277", 400, 220), tok("15", 500, 220), tok("6", 600, 220), tok("7", 700, 220),
        tok("Leave match", 400, 400, w=200),
    ]
    rows = build_rows(header + body, find_header(header))
    assert [r.name for r in rows] == ["KOOLTKK", "DEX"]
    assert rows[0].values["acs"][0] == 432 and rows[1].values["assists"][0] == 7


def test_read_score_trusts_outcome_banner_over_order():
    tokens = [tok("1", 300, 50, h=60), tok("13", 600, 50, h=60)]
    ally, enemy = read_score(tokens, "victory")
    assert (ally.value, enemy.value) == (13, 1)


def test_roster_matching_and_ally_team_vote():
    rosters = Rosters(
        team1=[RosterPlayer(user_id="u1", names=["Kooltkk#EU1"]), RosterPlayer(user_id="u2", names=["Dex"])],
        team2=[RosterPlayer(user_id="u3", names=["MrNobody"])],
    )
    matches = match_names(["KOOLTKK", "DEX", "MЯNOЪODY", "RANDOM"], rosters)
    assert [m.user_id if m else None for m in matches] == ["u1", "u2", "u3", None]
    team, conf = resolve_ally_team(["ally", "ally", "enemy", "enemy"], matches)
    assert team == "team1" and conf == 1.0


def _player(side, kills, deaths):
    stats = {"kills": OcrField[float](value=kills, confidence=1), "deaths": OcrField[float](value=deaths, confidence=1)}
    return PlayerRow(side=side, side_confidence=1, name=OcrField[str](value="x", confidence=1), agent=OcrField(), stats=stats,
                     roster_match=RosterMatch(user_id="u", team="team1", matched_name="x", score=1))


def test_checks_flag_mismatched_kills_and_odd_scores():
    players = [_player("ally", 10, 1), _player("enemy", 1, 2)]
    assert [w.code for w in check_kills(players)] == ["KILLS_DEATHS_MISMATCH"]
    assert check_score(13, 11) == [] and check_score(15, 13) == []
    assert [w.code for w in check_score(13, 12)] == ["SCORE_UNUSUAL"]
