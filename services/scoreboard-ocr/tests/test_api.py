import io

import cv2
import pytest
from fastapi.testclient import TestClient

from app import main
from app.models import MapRef, RosterPlayer, Rosters
from app.valorant.catalog import Catalog
from tests.synthetic import SAMPLE, make_agent_templates, render

pytest.importorskip("rapidocr")
TOKEN = "test-token"


@pytest.fixture(scope="module")
def client():
    from app.engine import RapidOcrEngine

    templates = make_agent_templates()
    main.configure(RapidOcrEngine(), Catalog(agents=templates, maps=[MapRef(name="Ascent")]), TOKEN)
    image = render(SAMPLE, templates, 13, 1, "VICTORY", "ASCENT")
    with TestClient(main.app) as c:
        c.png = cv2.imencode(".png", image)[1].tobytes()  # type: ignore[attr-defined]
        yield c


def _rosters() -> str:
    return Rosters(team1=[RosterPlayer(user_id="u1", names=["KOOLTKK"])]).model_dump_json(by_alias=True)


def test_requires_service_token(client):
    resp = client.post("/v1/valorant/scoreboard", files={"image": ("s.png", client.png, "image/png")})
    assert resp.status_code == 401


def test_rejects_non_images(client):
    resp = client.post(
        "/v1/valorant/scoreboard",
        headers={"X-Service-Token": TOKEN},
        files={"image": ("s.png", io.BytesIO(b"not an image"), "image/png")},
    )
    assert resp.status_code == 415


def test_rejects_bad_rosters(client):
    resp = client.post(
        "/v1/valorant/scoreboard",
        headers={"X-Service-Token": TOKEN},
        files={"image": ("s.png", client.png, "image/png")},
        data={"rosters": "{not json"},
    )
    assert resp.status_code == 422


def test_parses_scoreboard_as_camel_case_json(client):
    resp = client.post(
        "/v1/valorant/scoreboard",
        headers={"X-Service-Token": TOKEN},
        files={"image": ("s.png", client.png, "image/png")},
        data={"rosters": _rosters()},
    )
    assert resp.status_code == 200
    body = resp.json()
    assert body["allyScore"]["value"] == 13 and body["enemyScore"]["value"] == 1
    assert body["players"][0]["rosterMatch"]["userId"] == "u1"
    assert "firstBloods" in body["players"][0]["stats"]
