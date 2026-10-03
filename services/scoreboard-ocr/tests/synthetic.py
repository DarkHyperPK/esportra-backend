"""Render a Valorant-style post-match scoreboard with known values (test fixture generator).

This is a stand-in for real screenshots: same structure (summary on top, header row, ten
rows sorted by ACS with ally/enemy/self tints and agent portraits). Accuracy on real client
screenshots is measured separately with tests/fixtures/real/*.png when those are provided.
"""

from __future__ import annotations

from dataclasses import dataclass

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

from app.models import AgentRef
from app.valorant.catalog import AgentTemplate

ALLY_RGB, SELF_RGB, ENEMY_RGB = (46, 120, 112), (176, 158, 64), (150, 48, 60)
COLUMNS = (("AVG COMBAT SCORE", 760), ("K", 960), ("D", 1040), ("A", 1120), ("ECON RATING", 1260), ("FIRST BLOODS", 1430), ("PLANTS", 1580), ("DEFUSES", 1700))
ROW_TOP, PITCH, ROW_H = 380, 64, 56


@dataclass(frozen=True)
class FakePlayer:
    name: str
    ally: bool
    agent: int
    acs: int
    k: int
    d: int
    a: int
    econ: int
    fb: int
    plants: int
    defuses: int
    is_self: bool = False


def font(size: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    return ImageFont.load_default(size=size)


def make_agent_templates(count: int = 6) -> list[AgentTemplate]:
    """Distinct synthetic 'portraits': coloured shapes on a transparent background."""
    palette = [(230, 80, 60), (60, 200, 90), (70, 110, 240), (240, 210, 60), (200, 70, 220), (60, 220, 230)]
    templates = []
    for i in range(count):
        img = Image.new("RGBA", (128, 128), (0, 0, 0, 0))
        draw = ImageDraw.Draw(img)
        color = palette[i % len(palette)] + (255,)
        if i % 3 == 0:
            draw.ellipse((16, 16, 112, 112), fill=color)
        elif i % 3 == 1:
            draw.polygon([(64, 8), (120, 120), (8, 120)], fill=color)
        else:
            draw.rectangle((20, 30, 108, 98), fill=color)
        draw.rectangle((40 + i * 8, 50, 60 + i * 8, 70), fill=(250, 250, 250, 255))
        bgra = cv2.cvtColor(np.asarray(img), cv2.COLOR_RGBA2BGRA)
        templates.append(AgentTemplate(AgentRef(uuid=f"agent-{i}", name=f"Agent{i}", role="Duelist"), bgra))
    return templates


def render(players: list[FakePlayer], templates: list[AgentTemplate], ally_score: int, enemy_score: int, outcome: str, map_name: str) -> np.ndarray:
    img = Image.new("RGB", (1920, 1080), (16, 20, 28))
    draw = ImageDraw.Draw(img)
    draw.text((960, 90), outcome, font=font(72), fill=(240, 240, 240), anchor="mm")
    draw.text((820, 190), str(ally_score), font=font(80), fill=(110, 230, 200), anchor="mm")
    draw.text((1100, 190), str(enemy_score), font=font(80), fill=(240, 100, 110), anchor="mm")
    draw.text((960, 270), map_name, font=font(30), fill=(220, 220, 220), anchor="mm")
    for title, x in COLUMNS:
        draw.text((x, 345), title, font=font(20), fill=(200, 200, 200), anchor="mm")
    for i, p in enumerate(sorted(players, key=lambda p: p.acs, reverse=True)):
        top = ROW_TOP + i * PITCH
        tint = SELF_RGB if p.is_self else ALLY_RGB if p.ally else ENEMY_RGB
        draw.rectangle((210, top, 1770, top + ROW_H), fill=tint)
        icon = Image.fromarray(cv2.cvtColor(templates[p.agent].icon_bgra, cv2.COLOR_BGRA2RGBA)).resize((ROW_H - 6, ROW_H - 6))
        img.paste(icon, (222, top + 3), icon)
        draw.text((300, top + ROW_H // 2), p.name, font=font(28), fill=(245, 245, 245), anchor="lm")
        values = (p.acs, p.k, p.d, p.a, p.econ, p.fb, p.plants, p.defuses)
        for (_, x), value in zip(COLUMNS, values):
            draw.text((x, top + ROW_H // 2), str(value), font=font(28), fill=(245, 245, 245), anchor="mm")
    return cv2.cvtColor(np.asarray(img), cv2.COLOR_RGB2BGR)


SAMPLE = [
    FakePlayer("KOOLTKK", True, 0, 432, 26, 6, 4, 88, 2, 1, 0, is_self=True),
    FakePlayer("DEX", True, 1, 277, 15, 6, 7, 70, 0, 2, 0),
    FakePlayer("THE NEW STAR", True, 2, 263, 14, 7, 3, 65, 4, 0, 1),
    FakePlayer("ALMALIKI", True, 3, 208, 9, 8, 5, 52, 0, 1, 0),
    FakePlayer("ALUCARD", True, 4, 124, 5, 6, 9, 40, 0, 0, 1),
    FakePlayer("MRNOBODY", False, 5, 191, 9, 13, 2, 45, 2, 0, 0),
    FakePlayer("KFC WORKER", False, 2, 156, 7, 14, 3, 38, 3, 1, 0),
    FakePlayer("MYTH", False, 4, 147, 6, 14, 1, 36, 1, 0, 0),
    FakePlayer("DARKHYPER", False, 1, 142, 6, 14, 4, 30, 1, 0, 0),
    FakePlayer("THEONE", False, 3, 121, 5, 14, 2, 28, 1, 0, 0),
]
