"""Agent and map reference data from valorant-api.com, cached on disk."""

from __future__ import annotations

import json
import logging
from dataclasses import dataclass
from pathlib import Path

import cv2
import httpx
import numpy as np

from app.models import AgentRef, MapRef

log = logging.getLogger(__name__)

AGENTS_URL = "https://valorant-api.com/v1/agents?isPlayableCharacter=true"
MAPS_URL = "https://valorant-api.com/v1/maps"
ALLOWED_ICON_HOST = "media.valorant-api.com"

# Used when the catalog cannot be fetched: map names still resolve, agents stay unknown.
FALLBACK_MAPS = (
    "Abyss", "Ascent", "Bind", "Breeze", "Corrode", "Fracture", "Haven", "Icebox", "Lotus", "Pearl", "Split", "Sunset",
)


@dataclass(frozen=True)
class AgentTemplate:
    ref: AgentRef
    icon_bgra: np.ndarray


@dataclass(frozen=True)
class Catalog:
    agents: list[AgentTemplate]
    maps: list[MapRef]


def fallback_catalog() -> Catalog:
    return Catalog(agents=[], maps=[MapRef(name=name) for name in FALLBACK_MAPS])


def load_catalog(cache_dir: Path, refresh: bool) -> Catalog:
    """Load from cache_dir; download first when refresh is set or the cache is empty."""
    manifest = cache_dir / "catalog.json"
    if refresh or not manifest.exists():
        try:
            _download(cache_dir)
        except (httpx.HTTPError, OSError, ValueError, KeyError) as exc:
            log.warning("valorant catalog download failed: %s", exc)
    if not manifest.exists():
        return fallback_catalog()
    return _read_cache(cache_dir)


def _download(cache_dir: Path) -> None:
    icons_dir = cache_dir / "agents"
    icons_dir.mkdir(parents=True, exist_ok=True)
    with httpx.Client(timeout=20.0, follow_redirects=False) as client:
        agents = client.get(AGENTS_URL).raise_for_status().json()["data"]
        maps = client.get(MAPS_URL).raise_for_status().json()["data"]
        agent_rows = []
        for agent in agents:
            icon_url = str(agent.get("displayIcon") or "")
            if httpx.URL(icon_url).host != ALLOWED_ICON_HOST:
                continue
            (icons_dir / f"{agent['uuid']}.png").write_bytes(client.get(icon_url).raise_for_status().content)
            role = (agent.get("role") or {}).get("displayName")
            agent_rows.append({"uuid": agent["uuid"], "name": agent["displayName"], "role": role})
    map_rows = [{"name": m["displayName"], "uuid": m["uuid"], "mapUrl": m.get("mapUrl")} for m in maps]
    (cache_dir / "catalog.json").write_text(json.dumps({"agents": agent_rows, "maps": map_rows}))


def _read_cache(cache_dir: Path) -> Catalog:
    data = json.loads((cache_dir / "catalog.json").read_text())
    agents: list[AgentTemplate] = []
    for row in data.get("agents", []):
        icon = cv2.imread(str(cache_dir / "agents" / f"{row['uuid']}.png"), cv2.IMREAD_UNCHANGED)
        if icon is None:
            continue
        if icon.ndim == 2:
            icon = cv2.cvtColor(icon, cv2.COLOR_GRAY2BGRA)
        elif icon.shape[2] == 3:
            icon = cv2.cvtColor(icon, cv2.COLOR_BGR2BGRA)
        agents.append(AgentTemplate(AgentRef(uuid=row["uuid"], name=row["name"], role=row.get("role")), icon))
    maps = [MapRef(name=m["name"], uuid=m.get("uuid"), map_url=m.get("mapUrl")) for m in data.get("maps", [])]
    return Catalog(agents=agents, maps=maps or fallback_catalog().maps)
