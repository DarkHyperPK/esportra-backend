"""Render a Valorant-style post-match Scoreboard tab with known values (test fixture generator).

Mirrors the real client layout (see tests/fixtures/real): "13 VICTORY 5" summary line, match info
and duration top right, the left tab menu, a KDA / FIRST BLOODS / PLANTS / DEFUSES table with teal
ally rows, muted purple enemy rows and a grey-olive "you" row, and a friends sidebar on the right.
"""

from __future__ import annotations

from dataclasses import dataclass

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

from app.models import AgentRef
from app.valorant.catalog import AgentTemplate

# Row tints measured on real screenshots (RGB): stat area / brighter name area.
ALLY_RGB, ALLY_NAME_RGB = (22, 67, 68), (28, 96, 92)
ENEMY_RGB, ENEMY_NAME_RGB = (63, 48, 70), (88, 52, 74)
SELF_RGB, SELF_NAME_RGB = (54, 60, 59), (80, 78, 66)
TABLE_X0, NAME_X1, TABLE_X1 = 388, 872, 1700
COLUMNS = (("KDA", 975), ("FIRST BLOODS", 1183), ("PLANTS", 1390), ("DEFUSES", 1597))
ROW_TOP, PITCH, ROW_H = 224, 48, 46


@dataclass(frozen=True)
class FakePlayer:
    name: str
    ally: bool
    agent: int
    k: int
    d: int
    a: int
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


def _summary(draw: ImageDraw.ImageDraw, ally_score: int, enemy_score: int, outcome: str, map_name: str) -> None:
    ally_colour, enemy_colour = (52, 230, 190), (240, 70, 80)
    x = 40
    for text, colour in ((str(ally_score), ally_colour), (outcome, (236, 232, 225)), (str(enemy_score), enemy_colour)):
        draw.text((x, 88), text, font=font(64), fill=colour, anchor="lm")
        x += int(draw.textlength(text, font=font(64))) + 18
    for i, line in enumerate(("OCT 3, 2026", "STANDARD - CUSTOM", f"MAP - {map_name.upper()}", "34:57")):
        draw.text((1740, 74 + i * 16), line, font=font(13), fill=(200, 200, 200) if i == 2 else (90, 96, 110), anchor="rm")
    for i, tab in enumerate(("SCOREBOARD", "HEAD TO HEAD", "TIMELINE")):
        draw.text((60, 202 + i * 48), tab, font=font(16), fill=(230, 230, 230), anchor="lm")


def render(players: list[FakePlayer], templates: list[AgentTemplate], ally_score: int, enemy_score: int, outcome: str, map_name: str) -> np.ndarray:
    img = Image.new("RGB", (1920, 1080), (18, 26, 40))
    draw = ImageDraw.Draw(img)
    _summary(draw, ally_score, enemy_score, outcome, map_name)
    draw.rectangle((TABLE_X0, 184, TABLE_X1, 218), fill=(30, 34, 46))
    draw.text((630, 201), "GROUPED BY TEAM", font=font(15), fill=(220, 220, 220), anchor="mm")
    for title, x in COLUMNS:
        draw.text((x, 201), title, font=font(15), fill=(220, 220, 220), anchor="mm")
    for i, p in enumerate(players):
        top = ROW_TOP + i * PITCH
        stat, name_bg = (SELF_RGB, SELF_NAME_RGB) if p.is_self else (ALLY_RGB, ALLY_NAME_RGB) if p.ally else (ENEMY_RGB, ENEMY_NAME_RGB)
        draw.rectangle((TABLE_X0, top, NAME_X1, top + ROW_H), fill=name_bg)
        draw.rectangle((NAME_X1, top, TABLE_X1, top + ROW_H), fill=stat)
        icon = Image.fromarray(cv2.cvtColor(templates[p.agent].icon_bgra, cv2.COLOR_BGRA2RGBA)).resize((ROW_H - 4, ROW_H - 4))
        img.paste(icon, (TABLE_X0 + 4, top + 2), icon)
        draw.text((449, top + ROW_H // 2), p.name, font=font(19), fill=(245, 245, 245), anchor="lm")
        values = (f"{p.k} / {p.d} / {p.a}", p.fb, p.plants, p.defuses)
        for (_, x), value in zip(COLUMNS, values):
            draw.text((x, top + ROW_H // 2), str(value), font=font(17), fill=(245, 245, 245), anchor="mm")
    for y in (300, 420, 560):  # friends sidebar
        draw.text((1846, y), "e", font=font(20), fill=(200, 200, 200), anchor="mm")
    return cv2.cvtColor(np.asarray(img), cv2.COLOR_RGB2BGR)


SAMPLE = [
    FakePlayer("KOOLTKK", True, 0, 26, 6, 4, 2, 1, 0, is_self=True),
    FakePlayer("DEX", True, 1, 15, 6, 7, 0, 2, 0),
    FakePlayer("THE NEW STAR", True, 2, 14, 7, 3, 4, 0, 1),
    FakePlayer("ALMALIKI", True, 3, 9, 8, 5, 0, 1, 0),
    FakePlayer("ALUCARD", True, 4, 5, 6, 9, 0, 0, 1),
    FakePlayer("MRNOBODY", False, 5, 9, 13, 2, 2, 0, 0),
    FakePlayer("KFC WORKER", False, 2, 7, 14, 3, 3, 1, 0),
    FakePlayer("MYTH", False, 4, 6, 14, 1, 1, 0, 0),
    FakePlayer("DARKHYPER", False, 1, 6, 14, 4, 1, 0, 0),
    FakePlayer("THEONE", False, 3, 5, 14, 2, 1, 0, 0),
]

STAT_FIELDS = (("kills", "k"), ("deaths", "d"), ("assists", "a"), ("firstBloods", "fb"), ("plants", "plants"), ("defuses", "defuses"))
