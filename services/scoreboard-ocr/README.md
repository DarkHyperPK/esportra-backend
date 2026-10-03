# scoreboard-ocr

An internal service that reads a Valorant post-match **Scoreboard** screenshot and returns structured, confidence-scored results. Only the Esportra API calls it (`POST /api/matches/{id}/reports/parse-screenshot`). It must not be exposed publicly.

## Engine

The service uses [RapidOCR](https://github.com/RapidAI/RapidOCR) on ONNX Runtime to run PaddleOCR's PP-OCR detection and recognition models.

- The models ship inside the `rapidocr` wheel, so nothing is downloaded at runtime and the service runs CPU-only with no PaddlePaddle dependency.
- `app/engine.py` hides the engine behind `OcrEngine`, so a native `paddleocr` engine can be added later if wanted.

## Pipeline (`app/valorant/`)

1. **`columns.py`** finds the header row from column titles (ACS / AVG COMBAT SCORE, K, D, A, K/D/A, ECON, FIRST BLOODS, PLANTS, DEFUSES, HS%, ADR). Missing columns come back as missing, not invented.
2. **`rows.py`** groups the text below the header into player rows and assigns each number to the nearest column. The name is the text left of the stats.
3. **`sides.py`** works out each row's team from its tint:
   - teal or green rows are allies;
   - the gold row is "you", also an ally;
   - red rows are enemies.
4. **`agents.py`** matches the portrait left of the name against agent icons from valorant-api.com. It uses a multi-scale template match and also requires the colours to agree.
5. **`header.py`** reads the outcome banner, the round score (each score digit is read twice), and the map name.
6. **`names.py`** fuzzy-matches names against the two rosters the API sends, handling stylised names like `MЯNOЪODY`. It then votes on which bracket team is the screenshot owner's side.
7. **`checks.py`** adds warnings: row counts, unknown sides, unmatched names, uncertain agents, kills that don't match the other team's deaths, unusual scores, and low-confidence cells.

## API

`POST /v1/valorant/scoreboard` takes `multipart/form-data` and needs an `X-Service-Token` header.

| Field | Meaning |
|---|---|
| `image` | PNG, JPEG or WebP, at most 10 MB, between 640×360 and 4K |
| `rosters` | JSON `{ "team1": [{ "userId", "names": [...] }], "team2": [...] }` |

Responses:

| Status | Meaning |
|---|---|
| 200 | `ScoreboardResult` (camelCase, see `app/models.py`) |
| 401 | Bad or missing token |
| 413 | File too large |
| 415 | Not an image we accept |
| 422 | No scoreboard found, or bad rosters |
| 504 | Timed out |

`GET /health` returns `{ status, agents }`.

## Configuration

| Env | Default | Purpose |
|---|---|---|
| `SCOREBOARD_OCR_TOKEN` | (required) | Shared secret, the same value as the API's `ScoreboardOcr__ServiceToken` |
| `SCOREBOARD_OCR_ASSET_DIR` | `/data/valorant-assets` | Cache for the agent icons and map list (mount a volume) |
| `SCOREBOARD_OCR_REFRESH_ASSETS` | `true` | Re-download the catalog on start. If the download fails, the cache is used. With no cache, map names still resolve but agents stay unknown. |
| `SCOREBOARD_OCR_TIMEOUT_SECONDS` | `25` | Time limit for one parse |
| `SCOREBOARD_OCR_MAX_CONCURRENT` | `2` | Parses queued per process |

## Deploy (Coolify)

- Build from `services/scoreboard-ocr/Dockerfile`. The service listens on port 8095.
- Attach it only to the internal network shared with the API, with no public domain.
- Set `ScoreboardOcr__BaseUrl=http://<service-name>:8095` and `ScoreboardOcr__ServiceToken` on the API.
- The image is about 800 MB. Each parse takes 1–3 s of CPU.

## Development

```bash
python -m venv .venv && . .venv/bin/activate
pip install -r requirements-dev.txt
pip install --no-deps -r requirements-ocr.txt   # same as the Dockerfile; keeps headless OpenCV
python -m pytest -q
```

### Tests

The tests render synthetic scoreboards with known values (`tests/synthetic.py`). They run them through the real OCR engine at several resolutions, including JPEG and WebP. This checks the pipeline and the parsing logic.

Accuracy on real client screenshots still has to be measured:

- Add real screenshots to `tests/fixtures/real/` as `<name>.png` with a matching `<name>.json` of expected values.
- Extend the tests to cover them.
- Target: at least 98% of numeric cells correct on clean, full-screen screenshots.
