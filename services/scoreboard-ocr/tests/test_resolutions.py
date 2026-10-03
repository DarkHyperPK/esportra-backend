"""Same scoreboard at other resolutions / JPEG compression, through the real decode path."""

import cv2
import pytest

from app.image_io import decode_image
from app.models import MapRef, Rosters
from app.valorant.catalog import Catalog
from app.valorant.pipeline import parse_scoreboard
from tests.synthetic import SAMPLE, make_agent_templates, render

pytest.importorskip("rapidocr")


@pytest.fixture(scope="module")
def engine():
    from app.engine import RapidOcrEngine

    return RapidOcrEngine()


@pytest.mark.parametrize(
    ("width", "ext", "params"),
    [(1280, ".jpg", [cv2.IMWRITE_JPEG_QUALITY, 80]), (2560, ".png", []), (1600, ".webp", [cv2.IMWRITE_WEBP_QUALITY, 85])],
)
def test_reads_scaled_and_compressed_screenshots(engine, width, ext, params):
    templates = make_agent_templates()
    image = render(SAMPLE, templates, 13, 1, "VICTORY", "ASCENT")
    scaled = cv2.resize(image, (width, round(1080 * width / 1920)), interpolation=cv2.INTER_AREA)
    data = cv2.imencode(ext, scaled, params)[1].tobytes()
    result = parse_scoreboard(decode_image(data), engine, Catalog(agents=templates, maps=[MapRef(name="Ascent")]), Rosters())
    expected = sorted(SAMPLE, key=lambda p: p.acs, reverse=True)
    cells = [(row.stats["acs"].value, exp.acs) for row, exp in zip(result.players, expected)]
    cells += [(row.stats["kills"].value, exp.k) for row, exp in zip(result.players, expected)]
    cells += [(row.stats["deaths"].value, exp.d) for row, exp in zip(result.players, expected)]
    accuracy = sum(a == b for a, b in cells) / len(cells)
    assert len(result.players) == 10 and accuracy >= 0.95
    assert [p.side for p in result.players] == ["ally" if e.ally else "enemy" for e in expected]
    assert (result.ally_score.value, result.enemy_score.value) == (13, 1)
