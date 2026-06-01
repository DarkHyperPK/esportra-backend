#!/usr/bin/env python3
"""
Ensure migrations that touch staging-only tables are safe on production-shaped DBs.

Production may not yet have seasons, catalog, or invitation tables. Any migration
that GRANTs, REVOKEs, or ALTERs those tables must guard with to_regclass / IF EXISTS.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
MIGRATIONS_DIR = (
    REPO_ROOT / "src" / "Esportra.Infrastructure" / "Migrations" / "Scripts"
)

# Tables that may exist on staging but not on production yet.
PROD_ABSENT_TABLES: frozenset[str] = frozenset(
    {
        "seasons",
        "season_nodes",
        "season_tournaments",
        "season_participants",
        "season_standings",
        "season_qualification_records",
        "season_point_rules",
        "season_advancement_rules",
        "season_staff",
        "season_announcements",
        "tournament_invitations",
        "game_catalog_games",
        "game_catalog_requests",
        "organization_game_catalog",
    }
)

_DDL_ON_TABLE = re.compile(
    r"\b(?:GRANT|REVOKE|ALTER\s+TABLE|DROP\s+TABLE|TRUNCATE\s+TABLE)\b[^;]*\bpublic\.(\w+)",
    re.IGNORECASE,
)
_TO_REGCLASS = re.compile(r"to_regclass\s*\(", re.IGNORECASE)
_IF_EXISTS = re.compile(r"\bIF\s+EXISTS\b", re.IGNORECASE)


def main() -> int:
    if not MIGRATIONS_DIR.is_dir():
        print(f"Missing migrations dir: {MIGRATIONS_DIR}", file=sys.stderr)
        return 1

    failures: list[str] = []

    for path in sorted(MIGRATIONS_DIR.glob("*.sql")):
        text = path.read_text(encoding="utf-8")
        referenced = {
            match.group(1).lower()
            for match in _DDL_ON_TABLE.finditer(text)
            if match.group(1).lower() in PROD_ABSENT_TABLES
        }
        if not referenced:
            continue

        guarded = _TO_REGCLASS.search(text) or _IF_EXISTS.search(text)
        if not guarded:
            tables = ", ".join(sorted(referenced))
            failures.append(
                f"{path.name}: references prod-absent table(s) [{tables}] "
                "without to_regclass() or IF EXISTS guard"
            )

    if failures:
        print("Prod-shaped migration lint failed:", file=sys.stderr)
        for line in failures:
            print(f"  - {line}", file=sys.stderr)
        return 1

    print("Prod-shaped migration lint passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
