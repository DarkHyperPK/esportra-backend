#!/usr/bin/env python3
"""
Audit legacy table dependencies required for CI post-baseline migration replay.

Scans post-baseline DbUp scripts for references to public tables that are never
CREATE TABLE'd in the migration corpus. Writes .github/ci/replay-legacy-manifest.json.

Usage:
  python .github/scripts/audit-replay-legacy-deps.py
  python .github/scripts/audit-replay-legacy-deps.py --check
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
MIGRATIONS_DIR = (
    REPO_ROOT / "src" / "Esportra.Infrastructure" / "Migrations" / "Scripts"
)
OUTPUT = REPO_ROOT / ".github" / "ci" / "replay-legacy-manifest.json"
BASELINE = "20260317_001_baseline.sql"

_lint_path = Path(__file__).resolve().parent / "lint-migrations.py"
_spec = importlib.util.spec_from_file_location("lint_migrations", _lint_path)
assert _spec and _spec.loader
_lint = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_lint)

canonical = _lint.canonical
collect_defined_tables = _lint.collect_defined_tables
extract_matches = _lint.extract_matches
strip_noise = _lint.strip_noise

INSERT_INTO = re.compile(
    r"\bINSERT\s+INTO\s+(?:(\w+)\.)?(\w+)\b",
    re.IGNORECASE,
)
UPDATE_TABLE = re.compile(
    r"\bUPDATE\s+(?:(\w+)\.)?(\w+)\b",
    re.IGNORECASE,
)
DELETE_FROM = re.compile(
    r"\bDELETE\s+FROM\s+(?:(\w+)\.)?(\w+)\b",
    re.IGNORECASE,
)
FROM_JOIN = re.compile(
    r"\b(?:FROM|JOIN)\s+(?:(\w+)\.)?(\w+)\b",
    re.IGNORECASE,
)
ON_CONFLICT = re.compile(
    r"\bON\s+CONFLICT\s*\(([^)]+)\)",
    re.IGNORECASE,
)

SKIP_TABLES = frozenset(
    {
        "pg_policies",
        "pg_constraint",
        "pg_enum",
        "pg_publication",
        "pg_publication_tables",
        "pg_roles",
        "pg_class",
        "pg_namespace",
        "numbered",
        "existing",
        "excluded",
        "new",
        "old",
    }
)

REFERENCE_PATTERNS: list[tuple[re.Pattern, str]] = [
    (_lint._ALTER_TABLE, "alter"),
    (_lint._CREATE_INDEX, "index"),
    (_lint._CREATE_POLICY, "policy"),
    (_lint._CREATE_TRIGGER, "trigger"),
    (_lint._REFERENCES, "references"),
    (INSERT_INTO, "insert"),
    (UPDATE_TABLE, "update"),
    (DELETE_FROM, "delete"),
    (FROM_JOIN, "join"),
]


def post_baseline_files(files: list[Path]) -> list[Path]:
    return [f for f in files if f.name > BASELINE]


def record_table(
    entries: dict[str, dict],
    table: str,
    migration: str,
    operation: str,
) -> None:
    if table in SKIP_TABLES:
        return
    entry = entries.setdefault(
        table,
        {
            "firstSeenIn": migration,
            "operations": [],
            "columnsMentioned": [],
            "onConflictColumns": [],
        },
    )
    if migration < entry["firstSeenIn"]:
        entry["firstSeenIn"] = migration
    if operation not in entry["operations"]:
        entry["operations"].append(operation)


def record_column(entries: dict[str, dict], table: str, column: str) -> None:
    if table not in entries:
        return
    cols = entries[table]["columnsMentioned"]
    if column not in cols:
        cols.append(column)


def audit_legacy_tables(files: list[Path]) -> dict[str, dict]:
    defined = collect_defined_tables(files)
    entries: dict[str, dict] = {}

    for sql_file in post_baseline_files(files):
        content = strip_noise(
            sql_file.read_text(encoding="utf-8", errors="replace")
        )

        for pattern, operation in REFERENCE_PATTERNS:
            for schema, table in extract_matches(content, pattern):
                name = canonical(schema, table)
                if name and name not in defined:
                    record_table(entries, name, sql_file.name, operation)

        for match in ON_CONFLICT.finditer(content):
            cols = [c.strip().strip('"') for c in match.group(1).split(",")]
            for table_name, entry in entries.items():
                for col in cols:
                    if col and col not in entry["onConflictColumns"]:
                        entry["onConflictColumns"].append(col)

    for entry in entries.values():
        entry["operations"].sort()
        entry["columnsMentioned"].sort()
        entry["onConflictColumns"].sort()

    return dict(sorted(entries.items()))


def build_manifest(files: list[Path]) -> dict:
    legacy = audit_legacy_tables(files)
    return {
        "baseline": BASELINE,
        "generatedBy": ".github/scripts/audit-replay-legacy-deps.py",
        "postBaselineScriptCount": len(post_baseline_files(files)),
        "legacyTables": legacy,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="Exit 1 if committed manifest differs from generated output",
    )
    args = parser.parse_args()

    if not MIGRATIONS_DIR.is_dir():
        print(f"ERROR: Missing migrations dir: {MIGRATIONS_DIR}", file=sys.stderr)
        return 1

    files = sorted(MIGRATIONS_DIR.glob("*.sql"), key=lambda p: p.name)
    manifest = build_manifest(files)
    rendered = json.dumps(manifest, indent=2) + "\n"

    if args.check:
        if not OUTPUT.is_file():
            print(f"ERROR: Missing manifest: {OUTPUT}", file=sys.stderr)
            return 1
        if OUTPUT.read_text(encoding="utf-8") != rendered:
            print(
                f"ERROR: {OUTPUT} is stale. "
                "Run: python .github/scripts/audit-replay-legacy-deps.py",
                file=sys.stderr,
            )
            return 1
        print(
            f"Replay legacy manifest is up to date "
            f"({len(manifest['legacyTables'])} legacy table(s))."
        )
        return 0

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(rendered, encoding="utf-8")
    print(
        f"Wrote {OUTPUT} ({len(manifest['legacyTables'])} legacy table(s), "
        f"{manifest['postBaselineScriptCount']} post-baseline scripts)"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
