"""Recognise the agent from a row's portrait.

Two tiers:
1. A bank of real scoreboard portraits (assets/agent_bank/<Agent>/*.png, labelled from player
   screenshots). The in-game portrait is the same artwork at the same framing every time, so a
   nearest-neighbour vote on 12x12 colour thumbnails is ~99% accurate, also on JPEG-compressed,
   downscaled and stretched captures (measured leave-one-screenshot-out, see README).
2. Icon art (valorant-api display icons and the bundled portraits of new agents). Framed differently
   from the in-game portrait, so it is compared on gradients (HOG) and never returns high confidence.
   It covers agents the bank has no crops of yet.

Features are resized to fixed squares, so a horizontally stretched capture (4:3 stretched to 16:9)
compares like a native one.
"""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass, field
from functools import lru_cache
from pathlib import Path

import cv2
import numpy as np

ASSETS = Path(__file__).parent / "assets"
BANK_DIR = ASSETS / "agent_bank"
PORTRAIT_DIR = ASSETS / "portraits"

# Bank tier: accept the nearest crop below this distance (calibrated on the labelled crops: keeps 94% of
# known agents, rejects 90% of agents the bank has never seen).
BANK_MAX_DISTANCE = 0.5
# ...or below this one when the next agent is this many times further away.
BANK_FAR_DISTANCE = 0.8
BANK_FAR_MARGIN = 1.3
ICON_MAX_DISTANCE = 0.7
ICON_MIN_MARGIN = 1.06
K = 3


def _drop_stripe(img: np.ndarray) -> np.ndarray:
    """Rows start with a thin ally/enemy accent stripe; it says nothing about the agent."""
    return img[:, int(0.08 * img.shape[1]) :] if img.shape[1] > 12 else img


def thumb_features(img_bgr: np.ndarray) -> np.ndarray:
    small = cv2.resize(_drop_stripe(img_bgr), (12, 12), interpolation=cv2.INTER_AREA).astype(np.float32).ravel()
    small -= small.mean()
    return small / (np.linalg.norm(small) + 1e-6)


# Small trims (left, right, top, bottom as a share of the crop). Bank crops and queries are both
# compared as these windows, so a box that is a few pixels off (or a stretch estimate that is slightly
# wrong) still lines up with some variant.
_TRIMS = ((0, 0, 0, 0), (0.06, 0, 0, 0), (0, 0.06, 0, 0), (0, 0, 0.06, 0), (0, 0, 0, 0.06), (0.03, 0.03, 0.03, 0.03))


VARIANTS = len(_TRIMS)


def thumb_variants(img_bgr: np.ndarray) -> np.ndarray:
    h, w = img_bgr.shape[:2]
    out = []
    for left, right, top, bottom in _TRIMS:
        window = img_bgr[int(top * h) : h - int(bottom * h), int(left * w) : w - int(right * w)]
        out.append(thumb_features(window))
    return np.stack(out)


def hog_features(img_bgr: np.ndarray, cell: int = 8, bins: int = 9) -> np.ndarray:
    gray = cv2.cvtColor(cv2.resize(_drop_stripe(img_bgr), (32, 32), interpolation=cv2.INTER_AREA), cv2.COLOR_BGR2GRAY)
    g = gray.astype(np.float32)
    gx = cv2.Sobel(g, cv2.CV_32F, 1, 0, ksize=1)
    gy = cv2.Sobel(g, cv2.CV_32F, 0, 1, ksize=1)
    mag = np.hypot(gx, gy)
    idx = np.minimum((np.degrees(np.arctan2(gy, gx)) % 180 / (180 / bins)).astype(int), bins - 1)
    cells = [
        np.bincount(idx[y : y + cell, x : x + cell].ravel(), mag[y : y + cell, x : x + cell].ravel(), bins)
        for y in range(0, 32, cell)
        for x in range(0, 32, cell)
    ]
    out = np.concatenate(cells)
    return out / (np.linalg.norm(out) + 1e-6)


def flatten_icon(icon: np.ndarray) -> np.ndarray:
    """Icons with transparency are composited on the dark scoreboard background."""
    if icon.ndim == 2:
        return cv2.cvtColor(icon, cv2.COLOR_GRAY2BGR)
    if icon.shape[2] == 4:
        alpha = icon[..., 3:4].astype(np.float32) / 255.0
        back = np.full_like(icon[..., :3], 30, dtype=np.float32)
        return (icon[..., :3] * alpha + back * (1 - alpha)).astype(np.uint8)
    return icon


@dataclass(frozen=True)
class AgentGuess:
    name: str | None
    confidence: float
    tier: str  # "bank", "icon" or "none"


@dataclass
class AgentBank:
    names: list[str] = field(default_factory=list)
    sources: list[str] = field(default_factory=list)
    thumbs: np.ndarray = field(default_factory=lambda: np.zeros((0, 432), np.float32))
    icon_names: list[str] = field(default_factory=list)
    icon_hogs: np.ndarray = field(default_factory=lambda: np.zeros((0, 144), np.float32))

    @property
    def agents(self) -> set[str]:
        return set(self.names)

    @classmethod
    def from_crops(cls, crops: list[tuple[str, str, np.ndarray]]) -> AgentBank:
        """A bank from (agent name, source id, in-game portrait crop) triples."""
        names, sources, thumbs = [], [], []
        for name, source, img in crops:
            for variant in thumb_variants(img):
                names.append(name)
                sources.append(source)
                thumbs.append(variant)
        return cls(names, sources, np.stack(thumbs) if thumbs else np.zeros((0, 432), np.float32))

    def with_icons(self, icons: dict[str, np.ndarray]) -> AgentBank:
        """A copy with extra icon art (name -> BGR/BGRA image) in the icon tier."""
        names = list(self.icon_names)
        hogs = [*self.icon_hogs]
        for name, icon in icons.items():
            names.append(name)
            hogs.append(hog_features(flatten_icon(icon)))
        stacked = np.stack(hogs) if hogs else self.icon_hogs
        return AgentBank(self.names, self.sources, self.thumbs, names, stacked)

    def without_source(self, prefix: str) -> AgentBank:
        """The bank minus crops taken from one screenshot (leave-one-out evaluation)."""
        keep = [i for i, s in enumerate(self.sources) if not s.startswith(prefix)]
        return AgentBank(
            [self.names[i] for i in keep], [self.sources[i] for i in keep], self.thumbs[keep], self.icon_names, self.icon_hogs
        )

    def identify(self, portrait_bgr: np.ndarray) -> AgentGuess:
        if portrait_bgr.size == 0 or min(portrait_bgr.shape[:2]) < 8:
            return AgentGuess(None, 0.0, "none")
        guess = self._from_bank(portrait_bgr)
        return guess if guess.name else self._from_icons(portrait_bgr)

    def _from_bank(self, portrait: np.ndarray) -> AgentGuess:
        if not self.names:
            return AgentGuess(None, 0.0, "none")
        queries = thumb_variants(portrait)
        entry = np.min(np.linalg.norm(self.thumbs[None, :, :] - queries[:, None, :], axis=2), axis=0)
        # Variants of one crop are stored together: keep each crop's best variant, then vote per crop.
        per_crop = entry.reshape(-1, VARIANTS).min(axis=1)
        crop_names = self.names[::VARIANTS]
        order = np.argsort(per_crop)
        best = float(per_crop[order[0]])
        votes = Counter(crop_names[i] for i in order[:K])
        name, count = votes.most_common(1)[0]
        name = name if count > 1 else crop_names[order[0]]
        other = next((float(per_crop[i]) for i in order if crop_names[i] != name), best * 3)
        clearly_ahead = best < BANK_FAR_DISTANCE and other / max(best, 1e-6) > BANK_FAR_MARGIN
        if best >= BANK_MAX_DISTANCE and not clearly_ahead:
            return AgentGuess(None, 0.0, "none")
        closeness = float(np.clip((BANK_FAR_DISTANCE - best) / 0.6, 0, 1))
        margin = float(np.clip((other / max(best, 1e-6) - 1.2) / 0.8, 0, 1))
        return AgentGuess(name, round(0.5 + 0.5 * closeness * margin, 3), "bank")

    def _from_icons(self, portrait: np.ndarray) -> AgentGuess:
        if not self.icon_names:
            return AgentGuess(None, 0.0, "none")
        dist = np.linalg.norm(self.icon_hogs - hog_features(portrait), axis=1)
        order = np.argsort(dist)
        best = float(dist[order[0]])
        name = self.icon_names[order[0]]
        other = next((float(dist[i]) for i in order if self.icon_names[i] != name), best * 2)
        if best >= ICON_MAX_DISTANCE or other / max(best, 1e-6) < ICON_MIN_MARGIN:
            return AgentGuess(None, 0.0, "none")
        return AgentGuess(name, 0.45, "icon")  # below the review threshold on purpose


def display_name(folder: str) -> str:
    return folder.replace("_", "/")


@lru_cache(maxsize=1)
def bundled_bank() -> AgentBank:
    crops = []
    for folder in sorted(p for p in BANK_DIR.iterdir() if p.is_dir()) if BANK_DIR.exists() else []:
        for path in sorted(folder.glob("*.png")):
            img = cv2.imread(str(path), cv2.IMREAD_COLOR)
            if img is not None:
                crops.append((display_name(folder.name), path.stem, img))
    portraits = {p.stem: cv2.imread(str(p), cv2.IMREAD_UNCHANGED) for p in sorted(PORTRAIT_DIR.glob("*.png"))}
    return AgentBank.from_crops(crops).with_icons({k: v for k, v in portraits.items() if v is not None})
