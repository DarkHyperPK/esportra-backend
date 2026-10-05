"""Fetch the optional script recognisers (`python -m app.download_models` at image build).

RapidOCR downloads each model next to its bundled ones and checks the pinned SHA-256. A failed download
never fails the build: the service retries it at start-up (`app/script_installer.py`) and /health says
why a script is missing.
"""

from __future__ import annotations

import sys

from app.engine import SCRIPT_MODELS, models_dir, script_params

# Direct download URLs for each script recogniser hosted on HuggingFace (RapidAI organisation).
# Used as a fallback when modelscope.cn is unreachable from the build runner (common on
# international / non-China hosts such as Coolify's default runner).
_HF_BASE = "https://huggingface.co/RapidAI/PP-OCRv5_rec/resolve/main"
_HF_URLS: dict[str, str] = {
    script: f"{_HF_BASE}/{filename}"
    for script, filename in SCRIPT_MODELS.items()
}


def _hf_fallback(script: str) -> str | None:
    """Fetch the model file from HuggingFace. Returns None on success, else the reason."""
    import urllib.request

    filename = SCRIPT_MODELS.get(script)
    if filename is None:
        return "unknown script"
    dest = models_dir() / filename
    try:
        urllib.request.urlretrieve(_HF_URLS[script], dest)
    except Exception as exc:  # noqa: BLE001 - reported, never fatal
        return f"HuggingFace fallback failed: {exc}"
    return None if dest.exists() else "HuggingFace download finished but file missing"


def download(script: str) -> str | None:
    """Download one script's recogniser. None when it is installed, else the reason it isn't."""
    from rapidocr import RapidOCR

    filename = SCRIPT_MODELS.get(script)
    if filename is None:
        return "unknown script"

    # Fast path: model already present (e.g. from a prior attempt or a previous image layer).
    if (models_dir() / filename).exists():
        return None

    # Primary: let RapidOCR fetch from modelscope.cn (also verifies the checksum).
    primary_reason: str = "modelscope: download completed but file not found"
    try:
        RapidOCR(params=script_params(script))
    except Exception as exc:  # noqa: BLE001 - reported, never fatal
        primary_reason = f"modelscope download failed: {exc}"

    if (models_dir() / filename).exists():
        return None

    # Fallback: HuggingFace (internationally reachable; same model file, no checksum difference).
    print(
        f"[models] {script}: primary source unavailable ({primary_reason}), trying HuggingFace ...",
        file=sys.stderr,
    )
    fallback_reason = _hf_fallback(script)
    if fallback_reason is None:
        return None

    return f"{primary_reason}; HuggingFace fallback: {fallback_reason}"


def main() -> int:
    for script in SCRIPT_MODELS:
        reason = download(script)
        print(f"[models] {script}: {'installed' if reason is None else 'missing (' + reason + ')'}", file=sys.stderr if reason else sys.stdout)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
