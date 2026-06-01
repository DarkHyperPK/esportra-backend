#!/usr/bin/env python3
"""
Generate .github/ci/replay-journal-seed.sql for CI post-baseline migration replay.

Marks all DbUp scripts through 20260317_001_baseline.sql as already applied so
Esportra.Migrator only replays post-baseline migrations on a fresh Postgres.

Usage:
  python .github/scripts/generate-replay-journal-seed.py          # write seed file
  python .github/scripts/generate-replay-journal-seed.py --check  # fail if stale
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
MIGRATIONS_DIR = (
    REPO_ROOT / "src" / "Esportra.Infrastructure" / "Migrations" / "Scripts"
)
OUTPUT = REPO_ROOT / ".github" / "ci" / "replay-journal-seed.sql"
BASELINE = "20260317_001_baseline.sql"
SCRIPT_PREFIX = "Esportra.Infrastructure.Migrations.Scripts."


def pre_baseline_scripts() -> list[str]:
    files = sorted(p.name for p in MIGRATIONS_DIR.glob("*.sql"))
    result: list[str] = []
    for name in files:
        result.append(name)
        if name == BASELINE:
            break
    if not result or result[-1] != BASELINE:
        print(f"ERROR: Baseline file {BASELINE} not found in migrations.", file=sys.stderr)
        sys.exit(1)
    return result


def render_seed(scripts: list[str]) -> str:
    inserts: list[str] = []
    for filename in scripts:
        full_name = f"{SCRIPT_PREFIX}{filename}"
        inserts.append(
            f"""INSERT INTO schemaversions (scriptname, applied)
SELECT '{full_name}', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = '{full_name}'
);"""
        )

    body = "\n\n".join(inserts)
    return f"""-- CI post-baseline replay journal seed (generated — do not apply in production)
-- Regenerate: python .github/scripts/generate-replay-journal-seed.py
--
-- Marks pre-baseline DbUp scripts as already applied so migration-replay only
-- tests incremental migrations after {BASELINE}.

CREATE TABLE IF NOT EXISTS schemaversions (
  schemaversionsid SERIAL PRIMARY KEY,
  scriptname TEXT NOT NULL,
  applied TIMESTAMPTZ NOT NULL
);

{body}

SELECT 'replay journal seed applied ({len(scripts)} scripts)' AS status;
"""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="Exit 1 if committed seed file differs from generated output",
    )
    args = parser.parse_args()

    if not MIGRATIONS_DIR.is_dir():
        print(f"ERROR: Missing migrations dir: {MIGRATIONS_DIR}", file=sys.stderr)
        return 1

    scripts = pre_baseline_scripts()
    sql = render_seed(scripts)

    if args.check:
        if not OUTPUT.is_file():
            print(f"ERROR: Missing seed file: {OUTPUT}", file=sys.stderr)
            return 1
        committed = OUTPUT.read_text(encoding="utf-8")
        if committed != sql:
            print(
                f"ERROR: {OUTPUT} is stale. "
                f"Run: python .github/scripts/generate-replay-journal-seed.py",
                file=sys.stderr,
            )
            return 1
        print(f"Journal seed is up to date ({len(scripts)} pre-baseline scripts).")
        return 0

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(sql, encoding="utf-8")
    print(f"Wrote {OUTPUT} ({len(scripts)} pre-baseline scripts through {BASELINE})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
