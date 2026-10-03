"""Internal scoreboard OCR service. Called only by the Esportra API over the private network."""

from __future__ import annotations

import asyncio
import hmac
import logging
import os
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import Depends, FastAPI, File, Form, Header, HTTPException, UploadFile
from fastapi.concurrency import run_in_threadpool
from pydantic import ValidationError

from app.engine import OcrEngine, RapidOcrEngine
from app.image_io import ImageRejected, decode_image
from app.models import Rosters, ScoreboardResult
from app.valorant.catalog import Catalog, load_catalog
from app.valorant.pipeline import ScoreboardNotFound, parse_scoreboard

log = logging.getLogger("scoreboard-ocr")

MAX_UPLOAD_BYTES = 10 * 1024 * 1024
MAX_ROSTERS_CHARS = 16_000
PARSE_TIMEOUT_SECONDS = float(os.getenv("SCOREBOARD_OCR_TIMEOUT_SECONDS", "25"))
MAX_CONCURRENT = int(os.getenv("SCOREBOARD_OCR_MAX_CONCURRENT", "2"))


class State:
    engine: OcrEngine | None = None
    catalog: Catalog | None = None
    token: str = ""
    gate: asyncio.Semaphore | None = None


state = State()


def configure(engine: OcrEngine, catalog: Catalog, token: str) -> None:
    if not token:
        raise RuntimeError("SCOREBOARD_OCR_TOKEN must be set")
    state.engine, state.catalog, state.token = engine, catalog, token
    state.gate = asyncio.Semaphore(MAX_CONCURRENT)


@asynccontextmanager
async def lifespan(_: FastAPI):
    if state.engine is None:
        cache = Path(os.getenv("SCOREBOARD_OCR_ASSET_DIR", "/data/valorant-assets"))
        refresh = os.getenv("SCOREBOARD_OCR_REFRESH_ASSETS", "true").lower() == "true"
        catalog = await run_in_threadpool(load_catalog, cache, refresh)
        configure(RapidOcrEngine(), catalog, os.getenv("SCOREBOARD_OCR_TOKEN", ""))
        log.info("scoreboard-ocr ready: %d agents, %d maps", len(catalog.agents), len(catalog.maps))
    yield


app = FastAPI(title="Esportra scoreboard OCR", docs_url=None, redoc_url=None, openapi_url=None, lifespan=lifespan)


def require_token(x_service_token: str = Header(default="")) -> None:
    if not state.token or not hmac.compare_digest(x_service_token.encode(), state.token.encode()):
        raise HTTPException(status_code=401, detail="unauthorized")


@app.get("/health")
async def health() -> dict[str, object]:
    catalog = state.catalog
    return {"status": "ok", "agents": len(catalog.agents) if catalog else 0}


@app.post("/v1/valorant/scoreboard", response_model=ScoreboardResult, response_model_by_alias=True, dependencies=[Depends(require_token)])
async def valorant_scoreboard(image: UploadFile = File(...), rosters: str = Form(default="{}")) -> ScoreboardResult:
    data = await image.read(MAX_UPLOAD_BYTES + 1)
    if len(data) > MAX_UPLOAD_BYTES:
        raise HTTPException(status_code=413, detail="Screenshot is larger than 10 MB.")
    if len(rosters) > MAX_ROSTERS_CHARS:
        raise HTTPException(status_code=422, detail="Roster payload too large.")
    try:
        parsed_rosters = Rosters.model_validate_json(rosters)
    except ValidationError as exc:
        raise HTTPException(status_code=422, detail="Invalid rosters payload.") from exc
    try:
        image_bgr = decode_image(data)
    except ImageRejected as exc:
        raise HTTPException(status_code=415, detail=str(exc)) from exc

    if state.gate is None or state.engine is None or state.catalog is None:
        raise HTTPException(status_code=503, detail="Service is starting.")
    async with state.gate:
        try:
            return await asyncio.wait_for(
                run_in_threadpool(parse_scoreboard, image_bgr, state.engine, state.catalog, parsed_rosters),
                timeout=PARSE_TIMEOUT_SECONDS,
            )
        except ScoreboardNotFound as exc:
            raise HTTPException(status_code=422, detail=str(exc)) from exc
        except TimeoutError as exc:
            raise HTTPException(status_code=504, detail="Reading the screenshot took too long.") from exc
