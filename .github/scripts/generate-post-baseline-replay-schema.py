#!/usr/bin/env python3
"""
Generate .github/ci/post-baseline-replay-schema.sql for CI post-baseline replay.

Concatenates the Supabase stub + all pre-baseline migration SQL (through baseline
inclusive). This reproduces the database state after pre-baseline migrations without
requiring pg_dump (use build-post-baseline-schema.sh when Postgres is available).

Usage:
  python .github/scripts/generate-post-baseline-replay-schema.py
  python .github/scripts/generate-post-baseline-replay-schema.py --check
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
STUB = REPO_ROOT / ".github" / "ci" / "supabase-replay-bootstrap.sql"
MIGRATIONS_DIR = (
    REPO_ROOT / "src" / "Esportra.Infrastructure" / "Migrations" / "Scripts"
)
OUTPUT = REPO_ROOT / ".github" / "ci" / "post-baseline-replay-schema.sql"
BASELINE = "20260317_001_baseline.sql"


def pre_baseline_paths() -> list[Path]:
    files = sorted(MIGRATIONS_DIR.glob("*.sql"), key=lambda p: p.name)
    result: list[Path] = []
    for path in files:
        result.append(path)
        if path.name == BASELINE:
            break
    if not result or result[-1].name != BASELINE:
        print(f"ERROR: Baseline file {BASELINE} not found.", file=sys.stderr)
        sys.exit(1)
    return result


def render_schema(stub_sql: str, migration_paths: list[Path]) -> str:
    sections = [
        "-- CI post-baseline replay schema snapshot (generated — do not apply in production)",
        "-- Regenerate: python .github/scripts/generate-post-baseline-replay-schema.py",
        "-- Or with Postgres: bash .github/scripts/build-post-baseline-schema.sh",
        "",
        "-- ── Supabase stub (legacy schema shells) ────────────────────────────────",
        stub_sql.rstrip(),
        "",
    ]

    for path in migration_paths:
        content = path.read_text(encoding="utf-8", errors="replace").rstrip()
        sections.extend(
            [
                f"-- ── {path.name} ────────────────────────────────────────────────────────",
                content,
                "",
            ]
        )

    sections.append(
        f"SELECT 'post-baseline replay schema applied ({len(migration_paths)} migrations)' AS status;"
    )
    return "\n".join(sections) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="Exit 1 if committed schema file differs from generated output",
    )
    args = parser.parse_args()

    if not STUB.is_file():
        print(f"ERROR: Missing stub: {STUB}", file=sys.stderr)
        return 1

    migration_paths = pre_baseline_paths()
    sql = render_schema(STUB.read_text(encoding="utf-8"), migration_paths)

    if args.check:
        if not OUTPUT.is_file():
            print(f"ERROR: Missing schema file: {OUTPUT}", file=sys.stderr)
            return 1
        if OUTPUT.read_text(encoding="utf-8") != sql:
            print(
                f"ERROR: {OUTPUT} is stale. "
                "Run: python .github/scripts/generate-post-baseline-replay-schema.py",
                file=sys.stderr,
            )
            return 1
        print(
            f"Post-baseline replay schema is up to date "
            f"({len(migration_paths)} pre-baseline migrations)."
        )
        return 0

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(sql, encoding="utf-8")
    print(
        f"Wrote {OUTPUT} ({len(sql)} bytes, "
        f"{len(migration_paths)} pre-baseline migrations through {BASELINE})"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
