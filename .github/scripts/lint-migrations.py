#!/usr/bin/env python3
"""
Migration forward-reference linter.

Scans migration SQL files in DbUp execution order (alphabetical by filename)
and reports cases where a file references a table that has not yet been created.

This prevents the class of production outage that occurred when
20260424203000_br_round_integrity_guards.sql referenced br_round_evidence
before 20260521140000_br_round_evidence.sql created it.

Stripping strategy before pattern matching:
  1. Remove -- line comments
  2. Remove /* */ block comments
  3. Remove dollar-quoted string literals ($$ ... $$ and $tag$ ... $tag$)
     so that DDL inside EXECUTE(...) strings is not flagged as a forward reference
  4. Remove single-quoted string literals so that string payloads aren't matched

Exit 0 on pass, 1 on any forward reference found.
"""

import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
MIGRATIONS_DIR = (
    REPO_ROOT / "src" / "Esportra.Infrastructure" / "Migrations" / "Scripts"
)

# Tables owned by Supabase/Postgres — our migrations can reference these freely.
BUILTIN_TABLES: frozenset[str] = frozenset(
    {
        # auth schema
        "users",
        "identities",
        "sessions",
        "refresh_tokens",
        "mfa_factors",
        "mfa_challenges",
        "mfa_amr_claims",
        "audit_log_entries",
        "flow_state",
        "saml_providers",
        "saml_relay_states",
        "sso_providers",
        "sso_domains",
        "one_time_tokens",
        # storage schema
        "buckets",
        "objects",
        "s3_multipart_uploads",
        "s3_multipart_uploads_parts",
        # DbUp / Supabase internals
        "schemaversions",
        "schema_migrations",
    }
)

# Schema prefixes that are entirely Supabase-managed; skip any table in these.
EXTERNAL_SCHEMAS: frozenset[str] = frozenset(
    {
        "auth",
        "storage",
        "extensions",
        "pg_catalog",
        "information_schema",
        "realtime",
        "vault",
    }
)

# ── Noise stripping ──────────────────────────────────────────────────────────
# Strip comments AND string literals before pattern matching so that DDL inside
# EXECUTE('...') or EXECUTE($sql$...$sql$) payloads is not flagged.

_LINE_COMMENT = re.compile(r"--[^\n]*")
_BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.DOTALL)
# Matches $tag$...$tag$ dollar-quoted strings (includes $$...$$)
_DOLLAR_QUOTE = re.compile(r"\$(\w*)\$.*?\$\1\$", re.DOTALL)
# Matches 'single-quoted strings' with '' escape sequences handled
_SINGLE_QUOTE = re.compile(r"'(?:[^']|'')*'")


def strip_noise(sql: str) -> str:
    """Remove comments and string literals to avoid matching DDL inside EXECUTE()."""
    sql = _LINE_COMMENT.sub("", sql)
    sql = _BLOCK_COMMENT.sub("", sql)
    sql = _DOLLAR_QUOTE.sub("$$", sql)   # replace with empty dollar-quote placeholder
    sql = _SINGLE_QUOTE.sub("''", sql)   # replace with empty string placeholder
    return sql


# ── DDL patterns ─────────────────────────────────────────────────────────────
#
# Every pattern has exactly two capturing groups: (schema?, tablename).
# Optional schema group yields None when absent — that is correct.

_CREATE_TABLE = re.compile(
    r"\bCREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(\w+)\.)?(\w+)\s*\(",
    re.IGNORECASE,
)

_ALTER_TABLE = re.compile(
    r"\bALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:(\w+)\.)?(\w+)\b",
    re.IGNORECASE,
)

_CREATE_INDEX = re.compile(
    r"\bCREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:CONCURRENTLY\s+)?"
    r"(?:IF\s+NOT\s+EXISTS\s+)?\w+\s+ON\s+(?:(\w+)\.)?(\w+)\b",
    re.IGNORECASE,
)

_CREATE_POLICY = re.compile(
    r"\bCREATE\s+(?:OR\s+REPLACE\s+)?POLICY\s+\S+\s+ON\s+(?:(\w+)\.)?(\w+)\b",
    re.IGNORECASE,
)

# Matches: CREATE [OR REPLACE] [CONSTRAINT] TRIGGER name timing event(s) ON schema.table
_CREATE_TRIGGER = re.compile(
    r"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:CONSTRAINT\s+)?TRIGGER\s+\w+\b.+?\bON\s+(?:(\w+)\.)?(\w+)\b",
    re.IGNORECASE | re.DOTALL,
)

# FK references: REFERENCES [schema.]table(
_REFERENCES = re.compile(
    r"\bREFERENCES\s+(?:(\w+)\.)?(\w+)\s*\(",
    re.IGNORECASE,
)

# Checks to run (pattern, human-readable label)
_REFERENCE_CHECKS: list[tuple[re.Pattern, str]] = [
    (_ALTER_TABLE, "ALTER TABLE"),
    (_CREATE_INDEX, "CREATE INDEX … ON"),
    (_CREATE_POLICY, "CREATE POLICY … ON"),
    (_CREATE_TRIGGER, "CREATE TRIGGER … ON"),
    (_REFERENCES, "REFERENCES"),
]

# ── DROP INDEX without DROP CONSTRAINT patterns ───────────────────────────────

_DROP_INDEX_PATTERN = re.compile(
    r"\bDROP\s+INDEX\s+(?:IF\s+EXISTS\s+)?(\w+)",
    re.IGNORECASE,
)

_DROP_CONSTRAINT_PATTERN = re.compile(
    r"\bDROP\s+CONSTRAINT\s+(?:IF\s+EXISTS\s+)?(\w+)",
    re.IGNORECASE,
)


# ── Helpers ──────────────────────────────────────────────────────────────────


def canonical(schema: str | None, table: str) -> str | None:
    """Return the canonical (lowercase) table name to track, or None to skip."""
    s = (schema or "").lower()
    t = table.lower()
    if s in EXTERNAL_SCHEMAS:
        return None
    if s not in ("", "public"):
        return None  # unknown non-public schema — skip
    if t in BUILTIN_TABLES:
        return None
    return t


def extract_matches(
    content: str, pattern: re.Pattern
) -> list[tuple[str | None, str]]:
    return [(m.group(1), m.group(2)) for m in pattern.finditer(content)]


def collect_defined_tables(files: list[Path]) -> set[str]:
    """Tables introduced by CREATE TABLE in this migration set (not legacy Supabase schema)."""
    defined: set[str] = set()
    for sql_file in files:
        content = strip_noise(
            sql_file.read_text(encoding="utf-8", errors="replace")
        )
        for schema, table in extract_matches(content, _CREATE_TABLE):
            name = canonical(schema, table)
            if name:
                defined.add(name)
    return defined


# ── DROP INDEX check ─────────────────────────────────────────────────────────


def check_drop_index_without_constraint(
    files: list[Path], allowed_files: set[str]
) -> list[str]:
    """Return errors for any DROP INDEX not preceded by DROP CONSTRAINT on the same name."""
    errors: list[str] = []
    for sql_file in files:
        if sql_file.name in allowed_files:
            continue
        content = strip_noise(
            sql_file.read_text(encoding="utf-8", errors="replace")
        )
        constraint_positions: dict[str, list[int]] = {}
        for m in _DROP_CONSTRAINT_PATTERN.finditer(content):
            name = m.group(1).lower()
            constraint_positions.setdefault(name, []).append(m.start())
        for m in _DROP_INDEX_PATTERN.finditer(content):
            index_name = m.group(1).lower()
            preceding = any(
                pos < m.start()
                for pos in constraint_positions.get(index_name, [])
            )
            if not preceding:
                errors.append(
                    f"  ❌  {sql_file.name}\n"
                    f"       DROP INDEX '{index_name}' has no preceding DROP CONSTRAINT IF EXISTS"
                    f" — wrap in: ALTER TABLE ... DROP CONSTRAINT IF EXISTS {index_name};"
                    f" before DROP INDEX IF EXISTS {index_name};"
                )
    return errors


# ── Main ─────────────────────────────────────────────────────────────────────


def main() -> int:
    if not MIGRATIONS_DIR.exists():
        print(f"ERROR: Migrations directory not found: {MIGRATIONS_DIR}")
        return 1

    files = sorted(MIGRATIONS_DIR.glob("*.sql"), key=lambda p: p.name)
    if not files:
        print("ERROR: No migration files found.")
        return 1

    EXCEPTIONS_FILE = REPO_ROOT / ".github" / "ci" / "lint-exceptions.txt"
    allowed_files: set[str] = set()
    if EXCEPTIONS_FILE.exists():
        for line in EXCEPTIONS_FILE.read_text().splitlines():
            line = line.strip()
            if line and not line.startswith("#"):
                allowed_files.add(line)

    # Pre-DbUp schema (profiles, venues, tournaments, …) is documented in
    # 20260317_001_baseline.sql but not recreated here. Only enforce ordering for
    # tables that this script corpus actually CREATE TABLEs.
    defined_tables = collect_defined_tables(files)

    known: set[str] = set()
    errors: list[str] = []

    for sql_file in files:
        content = strip_noise(
            sql_file.read_text(encoding="utf-8", errors="replace")
        )

        # Tables created in this file are available to the rest of this file.
        created_here: set[str] = set()
        for schema, table in extract_matches(content, _CREATE_TABLE):
            name = canonical(schema, table)
            if name:
                created_here.add(name)

        available = known | created_here

        for pattern, label in _REFERENCE_CHECKS:
            for schema, table in extract_matches(content, pattern):
                name = canonical(schema, table)
                if (
                    name
                    and name in defined_tables
                    and name not in available
                ):
                    errors.append(
                        f"  \u274c  {sql_file.name}\n"
                        f"       {label} \u2192 '{name}' is not yet created\n"
                        f"       (ensure the CREATE TABLE migration runs before this file)"
                    )

        known.update(created_here)

    # Deduplicate forward-reference errors while preserving insertion order.
    fwd_errors = list(dict.fromkeys(errors))

    # Check for DROP INDEX statements not covered by DROP CONSTRAINT.
    drop_index_errors = check_drop_index_without_constraint(files, allowed_files)

    if fwd_errors or drop_index_errors:
        if fwd_errors:
            print(
                f"Migration linter: {len(fwd_errors)} forward-reference issue(s) found\n"
            )
            for e in fwd_errors:
                print(e)
        if drop_index_errors:
            print(
                f"Migration linter: {len(drop_index_errors)} DROP INDEX issue(s) found\n"
            )
            for e in drop_index_errors:
                print(e)
        return 1

    print(
        f"PASS  Migration linter passed -- "
        f"{len(defined_tables)} managed table(s), "
        f"{len(known)} created in-order across {len(files)} script(s)."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
