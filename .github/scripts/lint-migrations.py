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
  3. Remove dynamic EXECUTE payloads so guarded dynamic DDL is not treated as
     normal migration source
  4. Remove remaining single-quoted string literals so string payloads are not matched

Exit 0 on pass, 1 on any forward reference found.
"""

import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
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
# Strip comments and dynamic EXECUTE payloads before pattern matching so that
# guarded dynamic DDL is not flagged. Keep DO $$ ... $$ blocks themselves,
# because many migrations perform real static ALTER/CREATE statements inside DO
# blocks and those must still be linted.

_LINE_COMMENT = re.compile(r"--[^\n]*")
_BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.DOTALL)
# Dynamic SQL payloads inside EXECUTE statements that should not be linted as
# normal DDL in the surrounding migration source.
_EXECUTE_DOLLAR_QUOTE = re.compile(
    r"\bEXECUTE\s+\$(\w*)\$.*?\$\1\$",
    re.IGNORECASE | re.DOTALL,
)
_EXECUTE_SINGLE_QUOTE = re.compile(
    r"\bEXECUTE\s+'(?:[^']|'')*'",
    re.IGNORECASE | re.DOTALL,
)
_EXECUTE_FORMAT_START = re.compile(r"\bEXECUTE\s+format\s*\(", re.IGNORECASE)
# Strip remaining ordinary string literals so references in predicates and
# diagnostic text do not create false positives.
_SINGLE_QUOTE = re.compile(r"'(?:[^']|'')*'")


def strip_execute_format_calls(sql: str) -> str:
    """Replace EXECUTE format(...) statements, handling nested parentheses safely."""
    parts: list[str] = []
    cursor = 0

    while match := _EXECUTE_FORMAT_START.search(sql, cursor):
        start = match.start()
        pos = match.end()
        depth = 1
        in_single_quote = False

        while pos < len(sql):
            char = sql[pos]

            if char == "'":
                if in_single_quote and pos + 1 < len(sql) and sql[pos + 1] == "'":
                    pos += 2
                    continue

                in_single_quote = not in_single_quote
                pos += 1
                continue

            if in_single_quote:
                pos += 1
                continue

            if char == "(":
                depth += 1
            elif char == ")":
                depth -= 1
                if depth == 0:
                    pos += 1
                    while pos < len(sql) and sql[pos].isspace():
                        pos += 1
                    if pos < len(sql) and sql[pos] == ";":
                        pos += 1

                    parts.append(sql[cursor:start])
                    parts.append("EXECUTE format();")
                    cursor = pos
                    break

            pos += 1
        else:
            parts.append(sql[cursor:])
            return "".join(parts)

    parts.append(sql[cursor:])
    return "".join(parts)


def strip_noise(sql: str) -> str:
    """Remove comments and dynamic string payloads before DDL pattern matching."""
    sql = _LINE_COMMENT.sub("", sql)
    sql = _BLOCK_COMMENT.sub("", sql)
    sql = _EXECUTE_DOLLAR_QUOTE.sub("EXECUTE $$", sql)
    sql = _EXECUTE_SINGLE_QUOTE.sub("EXECUTE ''", sql)
    sql = strip_execute_format_calls(sql)
    sql = _SINGLE_QUOTE.sub("''", sql)
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


# ── Main ─────────────────────────────────────────────────────────────────────


def main() -> int:
    if not MIGRATIONS_DIR.exists():
        print(f"ERROR: Migrations directory not found: {MIGRATIONS_DIR}")
        return 1

    files = sorted(MIGRATIONS_DIR.glob("*.sql"), key=lambda p: p.name)
    if not files:
        print("ERROR: No migration files found.")
        return 1

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
                if name and name not in available:
                    errors.append(
                        f"  \u274c  {sql_file.name}\n"
                        f"       {label} \u2192 '{name}' is not yet created\n"
                        f"       (ensure the CREATE TABLE migration runs before this file)"
                    )

        known.update(created_here)

    if errors:
        # Deduplicate while preserving insertion order.
        unique_errors = list(dict.fromkeys(errors))
        print(f"Migration linter: {len(unique_errors)} forward-reference issue(s) found\n")
        for e in unique_errors:
            print(e)
        return 1

    print(
        f"\u2705  Migration linter passed \u2014 "
        f"{len(known)} table(s) tracked across {len(files)} script(s)."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
