# scoreboard-ocr

An internal service that reads a Valorant post-match **Scoreboard** screenshot and returns structured, confidence-scored results. Only the Esportra API calls it (`POST /api/matches/{id}/reports/parse-screenshot`). It must not be exposed publicly.

## Engine

The service uses [RapidOCR](https://github.com/RapidAI/RapidOCR) on ONNX Runtime to run PaddleOCR's PP-OCR detection and recognition models, CPU only, with no PaddlePaddle dependency.

- **Default recogniser:** PP-OCRv6, shipped inside the `rapidocr` wheel. It reads Latin script, digits, Chinese and Japanese.
- **Arabic and Korean names:** read by the PP-OCRv5 recognisers for those scripts. They are downloaded during the Docker build (`app/download_models.py`, SHA-256 pinned by RapidOCR).
  - If that download fails, the build still succeeds. The service then retries in the background (after 5 s, 1 min, 5 min, then every refresh interval) and loads each recogniser as soon as it arrives, with no restart (`app/script_installer.py`).
  - `/health` lists installed `scripts` and, in `missingScripts`, why any is missing.
- **Swappable engine:** `app/engine.py` hides the engine behind `OcrEngine`.

## What it reads

The client's post-match **Scoreboard** tab in every layout seen so far:

| Layout | Columns |
|---|---|
| Competitive, 2026 client | PERFORMANCE SCORE (stored as ACS), KDA, TRADES, FIRST BLOODS, PLANTS, DEFUSES; rank badge between portrait and name |
| Custom game | KDA, FIRST BLOODS, PLANTS, DEFUSES; no rank badge |
| Older client (Apr 2026) | AVG COMBAT SCORE, KDA, ECON, FIRST BLOODS, PLANTS, DEFUSES; agent name printed under each player |

Both "grouped by team" and "individually sorted" work.

**Row tints:**
- teal = ally;
- maroon = enemy;
- gold = an ally party member;
- grey = the screenshot owner.

Columns the screenshot doesn't show come back missing, never invented.

## Pipeline (`app/valorant/`)

1. **`pipeline._read_layout`:** reads the frame and measures horizontal stretch from the shape of the single-digit stat cells (`layout.estimate_stretch`). A stretched or squeezed capture is straightened and read again.
2. **`columns.py`** finds the header from column titles.
3. **`rows.py`** groups text into player rows.
   - Ignored: the side menu, the friends sidebar (also between rows) and rank-badge glyphs left of names.
   - Digits a long name runs into ("Rawzu from temu394") are split off the name.
4. **`pipeline.recover_cells`** re-reads empty or uncertain cells from enlarged crops.
   - A K/D/A cell that lost a separator ("14/ 14 19") is always re-read.
   - When the name is drawn over the score's first digit ("One260" seen as "One60"), the cell is left empty for the captain rather than guessed.
5. **`name_reread.py`** re-reads unread or uncertain names from the name cell. It uses the default recogniser and every installed script recogniser, and keeps the most confident read.
6. **`sides.py`** classifies each row by tint.
   - Rows the colour rules can't place (colour-blind palettes, hover highlights) join the team whose rows they look most like.
   - A single unknown row is filled in when the split is 5/4.
7. **`layout.py` and `agent_bank.py`** find each row's portrait at the table's left edge and recognise the agent:
   - first from the agent name printed under the player (older layout);
   - then from a bank of 280 labelled in-game portraits (nearest-neighbour vote, crops augmented against small box errors);
   - then from icon art (valorant-api icons plus `assets/portraits/` for new agents), always at low confidence.
8. **`names.py`** matches names to the two rosters.
   - It ignores case, spacing, punctuation and OCR look-alikes, works in any script, and handles truncated names and Premier tags.
   - Each roster name carries its source: `riot_verified`, `riot_previous`, `riot_claimed` or `username`.
   - A matched Riot name is shown in its real spelling.
   - A row whose name can't be read gets its team's last unmatched player by **elimination**. This is flagged `method: "elimination"` and is never proof of identity.
9. **`header.py`** reads the banner score. Valorant's rules pick between readings; surrenders (e.g. 9–4) are accepted at lower confidence. It also reads the map and the match date (`playedOn`).
10. **`checks.py`** adds warnings: row counts, unknown sides, unmatched names, uncertain agents, kills vs deaths, unusual scores, low confidence, hidden names (streamer mode) and stretched captures.

## Measured accuracy

The set is 34 real client screenshots in `tests/fixtures/real/`:
- 29 from players' uploads plus 5 earlier ones;
- 16:9, 16:10, 4:3 and 5:4 at 1280–1920 px;
- competitive, custom and the older client layout;
- 12 maps including Summit.

| Field | Result |
|---|---|
| Score, outcome, map, sides | 34 / 34 screenshots exact |
| Numeric cells (K, D, A, ACS, trades, FB, plants, defuses, econ) | 2599 / 2599 (one more cell is hidden under a name in the game itself and comes back empty) |
| Names, with the Arabic/Korean recognisers installed | 338 / 338 matched to the right player, including all 13 Arabic names |
| Arabic/Hangul names without those recognisers | 11 / 13 matched by elimination, 0 wrong |
| Agents, leave-one-screenshot-out | 98.3% (the misses are agents with a single example in the set) |

**Robustness (`tests/test_real_robustness.py`):** every screenshot is also degraded six ways, 204 variants in total, and all pass:
- 1366 px JPEG at quality 70;
- 3440×1440 ultrawide;
- WebP at quality 60;
- **stretched** to 16:9;
- **squeezed**;
- **pillarboxed** with black bars.

## Edge cases and how they're handled

| Edge case | Handling |
|---|---|
| Any native resolution 1280×720 → 4K, 16:9 / 16:10 / 4:3 / 5:4 | Header-driven layout; images scaled to ≥1920 wide or ≥1080 tall |
| Stretched resolution (4:3/5:4 stretched to 16:9, or squeezed) | Stretch measured from digit shapes, image straightened, read again |
| Black bars (pillarbox / letterbox) | Trimmed before reading |
| Ultrawide, two monitors in one capture | Header search over the whole frame |
| JPEG/WebP compression, Discord re-encoding, low resolution | Enlarged re-reads of cells and names |
| Colour-blind modes, unusual gamma, hover highlight | Relative side fallback; unknown rows flagged |
| Friends sidebar, side menu, party bar, chat lines | Ignored by position |
| Wrong tab or not a scoreboard | 422 "Upload the in-game Scoreboard tab" |
| Cropped screenshot without the banner | Score comes back missing; the captain enters it |
| Surrender / overtime | Accepted; unusual scores flagged |
| Fewer than 10 rows (4v5) | Read; `ROW_COUNT` warning |
| Non-Latin, spaced, letter-spaced, numeric, tagged or truncated names | Script models, look-alike folding, tag stripping, partial matching |
| Name hiding the score's first digit | Score left empty, never guessed |
| Full-width or Unicode slashes and digits in a re-read ("23／26/6") | Numeric text is NFKC-normalised before parsing |
| Riot IDs: the scoreboard shows game names without the #tag | Names are only "name matches"; the API confirms each account from Riot's match record (by PUUID) after submit |
| Streamer mode (names hidden) | `NAMES_HIDDEN` warning |
| Player named after an agent ("Jett") | Read as a name; only most rows matching triggers the warning |
| New agent before valorant-api lists it | Bundled portrait; returned with `uuid: null` and its name |
| New map | Fallback list plus a valorant-api refresh every 6 h |
| Draw (competitive only) | Read as `draw`; tournaments never draw |
| Phone photo of a monitor | Best effort; low-confidence fields are highlighted |

## API

`POST /v1/valorant/scoreboard` takes `multipart/form-data` and needs an `X-Service-Token` header.

| Field | Meaning |
|---|---|
| `image` | PNG, JPEG or WebP, at most 10 MB, between 640×360 and 4K |
| `rosters` | JSON `{ "team1": [{ "userId", "candidates": [{ "name", "source" }], "names": [...] }], "team2": [...] }`. `names` (no source) is still accepted. |

Responses:

| Status | Meaning |
|---|---|
| 200 | `ScoreboardResult` (camelCase, see `app/models.py`) |
| 401 | Bad or missing token |
| 413 | File too large |
| 415 | Not an image we accept |
| 422 | No scoreboard found, or bad rosters |
| 504 | Timed out |

`GET /health` returns `{ status, agents, maps, bankAgents, catalogVersion, refreshedAt, scripts, missingScripts, parserVersion }`.

## Configuration

| Env | Default | Purpose |
|---|---|---|
| `SCOREBOARD_OCR_TOKEN` | (required) | Shared secret, the same value as the API's `ScoreboardOcr__ServiceToken` |
| `SCOREBOARD_OCR_ASSET_DIR` | `/data/valorant-assets` | Cache for agent icons and the map list (mount a volume) |
| `SCOREBOARD_OCR_REFRESH_ASSETS` | `true` | Download the catalog at start, then check valorant-api's version every `SCOREBOARD_OCR_REFRESH_HOURS` and refresh on a new patch |
| `SCOREBOARD_OCR_REFRESH_HOURS` | `6` | How often to check for a new game patch (0 = only at start) |
| `SCOREBOARD_OCR_TIMEOUT_SECONDS` | `25` | Time limit for one parse |
| `SCOREBOARD_OCR_MAX_CONCURRENT` | `2` | Parses queued per process |

## Deploy (Coolify)

- Build from `services/scoreboard-ocr/Dockerfile`. The build downloads the Arabic and Korean recognisers from modelscope.cn. The build log shows `[models] arabic: installed`; `/health` → `scripts` should list both. The service listens on port 8095.
- If they're missing, `/health` → `missingScripts` gives the reason. The service keeps retrying, so allowing outbound HTTPS to modelscope.cn is enough; no rebuild is needed.
- Attach it only to the internal network shared with the API, with no public domain.
- Set `ScoreboardOcr__BaseUrl=http://<service-name>:8095` and `ScoreboardOcr__ServiceToken` on the API.
- A parse takes 1–3 s of CPU; a stretched capture is read twice.

## Development

```bash
python -m venv .venv && . .venv/bin/activate
pip install -r requirements-dev.txt
pip install --no-deps -r requirements-ocr.txt   # same as the Dockerfile; keeps headless OpenCV
python -m app.download_models                   # optional: Arabic/Korean recognisers
python -m pytest -q
```

### Tests

- **`tests/test_real_screenshots.py`** runs every image in `tests/fixtures/real/` against its hand-written `<name>.json`.
  - Exact: score, outcome, map and sides.
  - At least 99% of numeric cells.
  - Every readable name matched to the right player.
  - Agents at least 95% leave-one-screenshot-out.
  - Run it with `-s` for the accuracy summary.
- **`tests/test_real_robustness.py`** reruns them degraded (see above).
- **`tests/test_v3_rules.py`** and **`tests/test_units.py`** cover the parsing rules without the OCR engine.
- **`tests/synthetic.py`** renders scoreboards with known values for the end-to-end, resolution and API tests.

### Adding screenshots or agents

1. Put the image (lossless WebP or PNG) and its `<name>.json` in `tests/fixtures/real/`.
2. To teach a new agent, add its in-game portrait crops to `app/valorant/assets/agent_bank/<Agent>/`, named `<fixture>__<row>.png` so leave-one-out still works.
3. Before valorant-api lists an agent, its icon goes in `app/valorant/assets/portraits/<Agent>.png`.
