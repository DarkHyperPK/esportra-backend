"""Identify each row's agent: the agent name printed under the player (old client layout), else the
portrait recognised by the agent bank (see agent_bank.py)."""

from __future__ import annotations

import numpy as np
from rapidfuzz import fuzz

from app.models import AgentRef, OcrField
from app.valorant.catalog import Catalog


def _key(name: str) -> str:
    return "".join(ch for ch in name.casefold() if ch.isalnum())


def resolve_ref(name: str, catalog: Catalog) -> AgentRef:
    """The catalog entry (with its valorant-api uuid) for an agent name. Agents valorant-api doesn't
    list yet come back without a uuid; the name is still shown."""
    for template in catalog.agents:
        if _key(template.ref.name) == _key(name):
            return template.ref
    return AgentRef(uuid=None, name=name)


def _from_hint(hint: str, catalog: Catalog) -> str | None:
    if not hint:
        return None
    names = {t.ref.name for t in catalog.agents} | catalog.bank.agents
    scored = [(fuzz.ratio(_key(hint), _key(n)), n) for n in names]
    best = max(scored, default=(0.0, None))
    return best[1] if best[0] >= 80 else None


def identify_agent(portrait_bgr: np.ndarray, catalog: Catalog, hint: str = "") -> OcrField[AgentRef]:
    hinted = _from_hint(hint, catalog)
    if hinted:
        return OcrField[AgentRef](value=resolve_ref(hinted, catalog), confidence=0.97)
    guess = catalog.bank.identify(portrait_bgr)
    if guess.name is None:
        return OcrField[AgentRef]()
    return OcrField[AgentRef](value=resolve_ref(guess.name, catalog), confidence=guess.confidence)
