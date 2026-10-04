"""Installs script recognisers the image was built without.

The Docker build downloads the Arabic and Korean recognisers, but never fails when the model host is
unreachable. Then the service starts without them, retries the download in the background and loads
each recogniser as soon as it arrives, with no restart.
"""

from __future__ import annotations

import asyncio
import logging
from collections.abc import Callable, Sequence
from typing import Protocol

from fastapi.concurrency import run_in_threadpool

log = logging.getLogger(__name__)

# First retries soon after start (the network may come up after the container), then on the slow cadence.
RETRY_DELAYS_SECONDS: tuple[float, ...] = (5, 60, 300)


class InstallableEngine(Protocol):
    missing: dict[str, str]

    def add_script(self, script: str) -> bool: ...


async def install_missing_scripts(
    engine: InstallableEngine,
    download: Callable[[str], str | None],
    delays: Sequence[float] = RETRY_DELAYS_SECONDS,
    every: float = 6 * 3600,
) -> None:
    """Retry until every missing recogniser is installed. `download` returns None or why it failed."""
    attempt = 0
    while engine.missing:
        await asyncio.sleep(delays[attempt] if attempt < len(delays) else every)
        attempt += 1
        for script in list(engine.missing):
            reason = await run_in_threadpool(download, script)
            if reason is None and await run_in_threadpool(engine.add_script, script):
                log.info("OCR %s recogniser installed", script)
            elif reason is not None:
                engine.missing[script] = reason
                log.warning("OCR %s recogniser still missing (attempt %d): %s", script, attempt, reason)
