# scoreboard-ocr

An internal service that reads a Valorant post-match **Scoreboard** screenshot and returns structured, confidence-scored results. Only the Esportra API calls it (`POST /api/matches/{id}/reports/parse-screenshot`). It must not be exposed publicly.

## Engine

The service uses [RapidOCR](https://github.com/RapidAI/RapidOCR) on ONNX Runtime to run PaddleOCR's PP-OCR detection and recognition models.

- The models ship inside the `rapidocr` wheel, so nothing is downloaded at runtime and the service runs CPU-only with no PaddlePaddle dependency.
- `app/engine.py` hides the engine behind `OcrEngine`, so a native `paddleocr` engine can be added later if wanted.

## What it reads

The Valorant client's post-match **Scoreboard** tab, either "grouped by team" or "individually sorted":

| Area | On screen | Read as |
|---|---|---|
| Summary line | `13 VICTORY 5` (owner's score on the left) | outcome, ally score, enemy score |
| Top right | `MAP - ASCENT` | map. The match length (`34:57`) is ignored. |
| Table columns | KDA (`20 / 7 / 3`), FIRST BLOODS, PLANTS, DEFUSES | kills, deaths, assists, firstBloods, plants, defuses |
| Row tint | teal = ally, muted purple = enemy, grey-olive = you | side |
| Name | `GTH \| tr1ck` | `tr1ck` (Premier tag dropped) |

The client scoreboard has no ACS, ADR or HS%, so those come back as missing, never invented.

## Pipeline (`app/valorant/`)

1. **`columns.py`** finds the header row from column titles. Older and other layouts (ACS, K, D, A, ECON, HS%, ADR) are recognised too. Columns that aren't found come back as missing, never invented.
2. **`rows.py`** groups the text below the header into player rows and assigns each value to the nearest column. It ignores the left tab menu and the friends sidebar. The name is the text cluster nearest the stats.
3. **`text.py`** parses the KDA cell. It tolerates dropped spaces, `|` read for `/`, and `/` misread as `1` (`141614` → 14/6/4). That last case comes back at low confidence so the captain checks it.
4. **`pipeline.fill_missing_cells`** re-reads empty single-value cells with recognition only, because the detector sometimes skips a lone thin `1`.
5. **`sides.py`** classifies each row from the median colour of its stats area. A single unknown row is filled in when the split is 5/4.
6. **`header.py`** reads the outcome banner and the digits beside it.
   - Several reads of each side are combined.
   - Valorant's rules (first to 13, overtime won by two, victory means the left score is higher) pick between readings, e.g. `15` vs `Y5` next to "VICTORY".
7. **`agents.py`** matches the portrait left of the name against agent icons from valorant-api.com. It uses a multi-scale template match and also requires the colours to agree.
8. **`names.py`** fuzzy-matches names against the two rosters the API sends. It handles stylised names (`MЯNOЪODY`) and a tag separator misread as `l` (`ARClaayan` → `aayan`), then votes on which bracket team is the screenshot owner's side.
9. **`checks.py`** adds warnings: row counts, unknown sides, unmatched names, uncertain agents, kills that don't match the other team's deaths, unusual scores, and low-confidence cells.

## Measured accuracy

Measured on the five real client screenshots in `tests/fixtures/real/` (Ascent, Lotus ×2, Haven ×2; three victories and two defeats; 1280–1920 px; PNG and WebP):

| Field | Result |
|---|---|
| Score, outcome, map | 5 / 5 correct |
| Row team (side) | 50 / 50 correct |
| K, D, A, first bloods, plants, defuses | 300 / 300 cells correct |
| Names | 48 / 50; the 2 misses are Hangul names |

### Known limits
- **Korean, Chinese and Japanese names are not read.** The bundled recognition model covers Latin script and digits. The captain fills those names in during review.
- **Agent recognition is untested on real icons.** It is only exercised with synthetic icons, because valorant-api.com is unreachable from the build sandbox. In production the icons download at start-up.
- **Phone photos of a monitor are best effort.** Angle, glare and moiré reduce accuracy.

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

- `tests/test_real_screenshots.py` runs every image in `tests/fixtures/real/` against its hand-written `<name>.json`. It requires exact score, outcome, map and sides, and at least 98% of numeric cells correct; it prints accuracy per file. Add new client layouts and resolutions here as they appear.
- `tests/synthetic.py` renders scoreboards in the same layout with known values, including the menu, duration and sidebar decoys. The end-to-end and resolution tests (1280 JPEG, 2560 PNG, 1600 WebP) use it.
- `tests/test_units.py` covers parsing rules without the OCR engine.
