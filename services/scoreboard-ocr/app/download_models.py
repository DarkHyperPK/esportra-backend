"""Fetch the optional script recognisers (`python -m app.download_models` at image build).

RapidOCR downloads each model next to its bundled ones and checks the pinned SHA-256. A failed download
never fails the build: the service retries it at start-up (`app/script_installer.py`) and /health says
why a script is missing.
"""

from __future__ import annotations

import sys

from app.engine import SCRIPT_MODELS, models_dir, script_params


def download(script: str) -> str | None:
    """Download one script's recogniser. None when it is installed, else the reason it isn't."""
    from rapidocr import RapidOCR

    filename = SCRIPT_MODELS.get(script)
    if filename is None:
        return "unknown script"
    try:
        RapidOCR(params=script_params(script))
    except Exception as exc:  # noqa: BLE001 - reported, never fatal
        return f"download failed: {exc}"
    return None if (models_dir() / filename).exists() else "download finished but the model file is missing"


def main() -> int:
    for script in SCRIPT_MODELS:
        reason = download(script)
        print(f"[models] {script}: {'installed' if reason is None else 'missing (' + reason + ')'}", file=sys.stderr if reason else sys.stdout)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
