"""The real screenshots again, degraded the way players' uploads are: low resolution + JPEG,
ultrawide (21:9), and lossy WebP. Scores and sides must survive; numbers must stay >= 95%."""

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


@pytest.fixture(scope="module")
def engine():
    from app.engine import RapidOcrEngine

    return RapidOcrEngine()


@pytest.mark.parametrize("degrade", [_low_res_jpeg, _ultrawide, _lossy_webp], ids=["1366-jpeg70", "ultrawide-3440", "webp60"])
@pytest.mark.parametrize("image_path", CASES, ids=[p.stem for p in CASES])
def test_degraded_real_screenshot(engine, image_path: Path, degrade):
    expected = json.loads(image_path.with_suffix(".json").read_text(encoding="utf-8"))
    img = cv2.imdecode(np.frombuffer(image_path.read_bytes(), np.uint8), cv2.IMREAD_COLOR)
    result = parse_scoreboard(decode_image(degrade(img)), engine, fallback_catalog(), Rosters())

    assert (result.ally_score.value, result.enemy_score.value) == (expected["ally"], expected["enemy"])
    assert [p.side for p in result.players] == [r["side"] for r in expected["rows"]]
    cells = [(p.stats[k].value, r[k]) for p, r in zip(result.players, expected["rows"]) for k in STATS]
    accuracy = sum(a == b for a, b in cells) / len(cells)
    print(f"{image_path.stem}/{degrade.__name__}: {accuracy:.1%}")
    assert accuracy >= 0.95
