"""Fetch the optional script recognisers into the image at build time (`python -m app.download_models`).

RapidOCR downloads each model next to its bundled ones and checks the pinned SHA-256. A failed download
never fails the build: the service then runs without that script and /health says so.
"""

from __future__ import annotations

import sys

from app.engine import SCRIPT_MODELS, models_dir, script_params


def main() -> int:
    from rapidocr import RapidOCR

    for script, filename in SCRIPT_MODELS.items():
        try:
            RapidOCR(params=script_params(script))
            ok = (models_dir() / filename).exists()
        except Exception as exc:  # noqa: BLE001 - reported, never fatal
            print(f"[models] {script}: download failed ({exc})", file=sys.stderr)
            ok = False
        print(f"[models] {script}: {'installed' if ok else 'missing'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
