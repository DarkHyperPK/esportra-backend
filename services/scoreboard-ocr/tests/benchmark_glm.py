"""Benchmark: current PaddleOCR pipeline vs GLM-4.6V-Flash on real scoreboard screenshots.

Usage:
    ZHIPU_API_KEY=your_key python tests/benchmark_glm.py [path_to_image]

Without a path, runs on all fixtures in tests/fixtures/real/.
"""

from __future__ import annotations

import base64
import json
import os
import sys
import time
from pathlib import Path

import httpx

ZHIPU_API_KEY = os.environ.get("ZHIPU_API_KEY", "")
ZHIPU_URL = "https://open.bigmodel.cn/api/paas/v4/chat/completions"
MODEL = "glm-4v-flash"

FIXTURES_DIR = Path(__file__).parent / "fixtures" / "real"

PROMPT = """You are analyzing a Valorant post-match scoreboard screenshot. Extract ALL data and return ONLY valid JSON (no markdown, no explanation).

Return this exact structure:
{
  "outcome": "victory" or "defeat" or "draw",
  "ally_score": <int>,
  "enemy_score": <int>,
  "map": "<map name>",
  "players": [
    {
      "name": "<player name as shown>",
      "agent": "<agent name>",
      "side": "ally" or "enemy",
      "kills": <int or null>,
      "deaths": <int or null>,
      "assists": <int or null>,
      "acs": <int or null>
    }
  ]
}

Rules:
- List all 10 players (5 ally, 5 enemy)
- "ally" = the team on top (teal/green tint), "enemy" = the team on bottom (red/maroon tint)
- Read player names exactly as shown, including special characters
- Identify agents from their portrait icons on the left of each row
- Extract K/D/A and ACS (or Performance Score) for each player
- If a value is unreadable, use null"""


def encode_image(path: Path) -> str:
    return base64.b64encode(path.read_bytes()).decode()


def call_glm(image_path: Path) -> dict:
    b64 = encode_image(image_path)
    suffix = image_path.suffix.lstrip(".")
    mime = {"png": "image/png", "jpg": "image/jpeg", "jpeg": "image/jpeg", "webp": "image/webp"}.get(suffix, "image/png")

    payload = {
        "model": MODEL,
        "messages": [
            {
                "role": "user",
                "content": [
                    {"type": "image_url", "image_url": {"url": f"data:{mime};base64,{b64}"}},
                    {"type": "text", "text": PROMPT},
                ],
            }
        ],
        "temperature": 0.1,
        "max_tokens": 2048,
    }

    start = time.monotonic()
    resp = httpx.post(
        ZHIPU_URL,
        headers={"Authorization": f"Bearer {ZHIPU_API_KEY}", "Content-Type": "application/json"},
        json=payload,
        timeout=60,
    )
    elapsed = time.monotonic() - start
    resp.raise_for_status()
    data = resp.json()
    content = data["choices"][0]["message"]["content"]

    # Strip markdown fences if present
    if content.startswith("```"):
        content = content.split("\n", 1)[1].rsplit("```", 1)[0]

    return {"result": json.loads(content), "latency_s": round(elapsed, 2), "tokens": data.get("usage", {})}


def run_pipeline(image_path: Path) -> dict:
    sys.path.insert(0, str(Path(__file__).parent.parent))
    from app.image_io import decode_image
    from app.engine import RapidOcrEngine
    from app.valorant.catalog import fallback_catalog
    from app.valorant.pipeline import parse_scoreboard
    from app.models import Rosters

    image_bytes = image_path.read_bytes()
    image_bgr = decode_image(image_bytes)
    engine = RapidOcrEngine(scripts=())
    catalog = fallback_catalog()

    start = time.monotonic()
    result = parse_scoreboard(image_bgr, engine, catalog, Rosters())
    elapsed = time.monotonic() - start

    players = []
    for p in result.players:
        players.append({
            "name": p.name.value,
            "agent": p.agent.value.name if p.agent.value else None,
            "side": p.side,
            "kills": p.stats.get("kills", None) and p.stats["kills"].value,
            "deaths": p.stats.get("deaths", None) and p.stats["deaths"].value,
            "assists": p.stats.get("assists", None) and p.stats["assists"].value,
            "acs": p.stats.get("acs", None) and p.stats["acs"].value,
        })

    return {
        "result": {
            "outcome": result.outcome.value,
            "ally_score": result.ally_score.value,
            "enemy_score": result.enemy_score.value,
            "map": result.map.value.name if result.map.value else None,
            "players": players,
        },
        "latency_s": round(elapsed, 2),
        "warnings": [w.message for w in result.warnings],
    }


def compare(image_path: Path) -> None:
    print(f"\n{'='*80}")
    print(f"  {image_path.name}")
    print(f"{'='*80}")

    # Pipeline
    print("\n--- PaddleOCR Pipeline ---")
    try:
        pipeline = run_pipeline(image_path)
        print(f"  Latency: {pipeline['latency_s']}s")
        print(f"  Score: {pipeline['result']['ally_score']}-{pipeline['result']['enemy_score']} ({pipeline['result']['outcome']})")
        print(f"  Map: {pipeline['result']['map']}")
        for p in pipeline["result"]["players"]:
            print(f"  [{p['side']:5s}] {p['agent'] or '?':12s} {p['name'] or '?':20s}  K:{p['kills']}  D:{p['deaths']}  A:{p['assists']}  ACS:{p['acs']}")
        if pipeline["warnings"]:
            print(f"  Warnings: {'; '.join(pipeline['warnings'][:3])}")
    except Exception as exc:
        print(f"  FAILED: {exc}")
        pipeline = None

    # GLM
    print("\n--- GLM-4.6V-Flash ---")
    if not ZHIPU_API_KEY:
        print("  SKIPPED: set ZHIPU_API_KEY env var")
        return
    try:
        glm = call_glm(image_path)
        print(f"  Latency: {glm['latency_s']}s  Tokens: {glm['tokens']}")
        print(f"  Score: {glm['result']['ally_score']}-{glm['result']['enemy_score']} ({glm['result']['outcome']})")
        print(f"  Map: {glm['result']['map']}")
        for p in glm["result"]["players"]:
            print(f"  [{p['side']:5s}] {p['agent'] or '?':12s} {p['name'] or '?':20s}  K:{p['kills']}  D:{p['deaths']}  A:{p['assists']}  ACS:{p['acs']}")
    except Exception as exc:
        print(f"  FAILED: {exc}")
        glm = None

    # Comparison
    if pipeline and glm:
        print("\n--- Comparison ---")
        pr, gr = pipeline["result"], glm["result"]
        score_match = (pr["ally_score"], pr["enemy_score"]) == (gr["ally_score"], gr["enemy_score"])
        print(f"  Score match: {'YES' if score_match else 'NO'}")
        print(f"  Map match: {'YES' if pr['map'] and gr['map'] and pr['map'].lower() == gr['map'].lower() else 'NO'}")
        name_matches = 0
        for pp in pr["players"]:
            if pp["name"] and any(gp["name"] and pp["name"].lower() in gp["name"].lower() or gp["name"].lower() in pp["name"].lower() for gp in gr["players"]):
                name_matches += 1
        print(f"  Name overlap: {name_matches}/{len(pr['players'])}")
        agent_matches = 0
        for pp in pr["players"]:
            if pp["agent"] and any(gp["agent"] and pp["agent"].lower() == gp["agent"].lower() for gp in gr["players"]):
                agent_matches += 1
        print(f"  Agent overlap: {agent_matches}/{len(pr['players'])}")
        print(f"  Latency: pipeline {pipeline['latency_s']}s vs GLM {glm['latency_s']}s")


def main() -> None:
    if len(sys.argv) > 1:
        paths = [Path(sys.argv[1])]
    else:
        paths = sorted(FIXTURES_DIR.glob("*.png"))

    if not paths:
        print("No images found. Pass a path or add PNGs to tests/fixtures/real/")
        return

    for path in paths:
        compare(path)


if __name__ == "__main__":
    main()
