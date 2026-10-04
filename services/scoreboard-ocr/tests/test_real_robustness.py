"""The real screenshots again, degraded the way players' uploads are: low resolution + JPEG,
ultrawide (21:9), lossy WebP, stretched and squeezed resolutions, and black side bars.
Scores and sides must survive; numbers must stay >= 95%."""

import json
from pathlib import Path

import cv2
import numpy as np
import pytest

from app.image_io import decode_image
from app.models import Rosters
from app.valorant.catalog import fallback_catalog
from app.valorant.pipeline import parse_scoreboard

pytest.importorskip("rapidocr")

FIXTURES = Path(__file__).parent / "fixtures" / "real"
CASES = sorted(p for p in FIXTURES.iterdir() if p.suffix in {".png", ".webp"})
STATS = ("kills", "deaths", "assists", "firstBloods", "plants", "defuses")


def _low_res_jpeg(img: np.ndarray) -> bytes:
    small = cv2.resize(img, (1366, round(img.shape[0] * 1366 / img.shape[1])), interpolation=cv2.INTER_AREA)
    return cv2.imencode(".jpg", small, [cv2.IMWRITE_JPEG_QUALITY, 70])[1].tobytes()


def _ultrawide(img: np.ndarray) -> bytes:
    tall = cv2.resize(img, (round(img.shape[1] * 1440 / img.shape[0]), 1440), interpolation=cv2.INTER_CUBIC)
    pad = max(0, (3440 - tall.shape[1]) // 2)
    wide = cv2.copyMakeBorder(tall, 0, 0, pad, pad, cv2.BORDER_CONSTANT, value=(40, 26, 18))
    return cv2.imencode(".png", wide)[1].tobytes()


def _lossy_webp(img: np.ndarray) -> bytes:
    return cv2.imencode(".webp", img, [cv2.IMWRITE_WEBP_QUALITY, 60])[1].tobytes()


def _stretched(img: np.ndarray) -> bytes:
    """Stretched resolution: the frame scaled to 16:9 (a 4:3 game shown full screen and captured
    from the monitor, or a 16:9 capture forced wider)."""
    height = img.shape[0]
    wide = cv2.resize(img, (max(round(height * 16 / 9), round(img.shape[1] * 1.15)), height), interpolation=cv2.INTER_LINEAR)
    return cv2.imencode(".jpg", wide, [cv2.IMWRITE_JPEG_QUALITY, 85])[1].tobytes()


def _squeezed(img: np.ndarray) -> bytes:
    """The opposite: a frame squashed narrower (16:9 shown on a 4:3 monitor)."""
    narrow = cv2.resize(img, (round(img.shape[1] * 0.78), img.shape[0]), interpolation=cv2.INTER_AREA)
    return cv2.imencode(".png", narrow)[1].tobytes()


def _pillarboxed(img: np.ndarray) -> bytes:
    """A 4:3 / 5:4 game captured on a 16:9 screen with black side bars."""
    height, width = img.shape[:2]
    pad = max(0, (round(height * 16 / 9) - width) // 2) or round(0.12 * width)
    return cv2.imencode(".png", cv2.copyMakeBorder(img, 0, 0, pad, pad, cv2.BORDER_CONSTANT, value=(0, 0, 0)))[1].tobytes()


@pytest.fixture(scope="module")
def engine():
    from app.engine import RapidOcrEngine

    return RapidOcrEngine()


@pytest.mark.parametrize(
    "degrade",
    [_low_res_jpeg, _ultrawide, _lossy_webp, _stretched, _squeezed, _pillarboxed],
    ids=["1366-jpeg70", "ultrawide-3440", "webp60", "stretched", "squeezed", "pillarboxed"],
)
@pytest.mark.parametrize("image_path", CASES, ids=[p.stem for p in CASES])
def test_degraded_real_screenshot(engine, image_path: Path, degrade):
    expected = json.loads(image_path.with_suffix(".json").read_text(encoding="utf-8"))
    img = cv2.imdecode(np.frombuffer(image_path.read_bytes(), np.uint8), cv2.IMREAD_COLOR)
    result = parse_scoreboard(decode_image(degrade(img)), engine, fallback_catalog(), Rosters())

    assert (result.ally_score.value, result.enemy_score.value) == (expected["ally"], expected["enemy"])
    assert [p.side for p in result.players] == [r["side"] for r in expected["rows"]]
    cells = [(p.stats[k].value, r[k]) for p, r in zip(result.players, expected["rows"]) for k in STATS if k in r]
    accuracy = sum(a == b for a, b in cells) / len(cells)
    print(f"{image_path.stem}/{degrade.__name__}: {accuracy:.1%}")
    assert accuracy >= 0.95
