#!/usr/bin/env python3
"""
Generate .github/ci/supabase-replay-bootstrap.sql for CI migration replay.

DbUp scripts assume a Supabase-shaped database. CI uses vanilla Postgres, so we
pre-create auth/storage stubs and legacy public tables referenced by migrations
but never CREATE TABLE'd in Scripts/.

Re-run after adding migrations that touch new legacy tables:
  python .github/scripts/generate-replay-bootstrap.py
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

import importlib.util

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
MIGRATIONS_DIR = REPO_ROOT / "src" / "Esportra.Infrastructure" / "Migrations" / "Scripts"
OUTPUT = REPO_ROOT / ".github" / "ci" / "supabase-replay-bootstrap.sql"

_lint_path = Path(__file__).resolve().parent / "lint-migrations.py"
_spec = importlib.util.spec_from_file_location("lint_migrations", _lint_path)
assert _spec and _spec.loader
_lint = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_lint)

ALTER_TABLE = _lint._ALTER_TABLE
CREATE_INDEX = _lint._CREATE_INDEX
CREATE_POLICY = _lint._CREATE_POLICY
CREATE_TRIGGER = _lint._CREATE_TRIGGER
REFERENCES = _lint._REFERENCES
canonical = _lint.canonical
collect_defined_tables = _lint.collect_defined_tables
extract_matches = _lint.extract_matches
strip_noise = _lint.strip_noise

_REFERENCE_PATTERNS = [
    ALTER_TABLE,
    CREATE_INDEX,
    CREATE_POLICY,
    CREATE_TRIGGER,
    REFERENCES,
]

# Extra tables referenced in FROM/JOIN inside functions — add manually if replay fails.
MANUAL_LEGACY_TABLES: frozenset[str] = frozenset(
    {
        "team_rosters",
        "team_roster_members",
        "pg_policies",  # view — skip
    }
)

SKIP_TABLES = frozenset({"pg_policies"})


def collect_referenced_public_tables(files: list[Path]) -> set[str]:
    referenced: set[str] = set()
    for sql_file in files:
        content = strip_noise(sql_file.read_text(encoding="utf-8", errors="replace"))
        for pattern in _REFERENCE_PATTERNS:
            for schema, table in extract_matches(content, pattern):
                name = canonical(schema, table)
                if name and name not in SKIP_TABLES:
                    referenced.add(name)
    referenced.update(MANUAL_LEGACY_TABLES - SKIP_TABLES)
    return referenced


def render_bootstrap(legacy_tables: set[str]) -> str:
    sorted_tables = sorted(legacy_tables)
    table_ddl = "\n".join(
        f"CREATE TABLE IF NOT EXISTS public.{name} (id UUID PRIMARY KEY DEFAULT gen_random_uuid());"
        for name in sorted_tables
    )

    return f"""-- CI migration replay bootstrap (generated — do not apply in production)
-- Regenerate: python .github/scripts/generate-replay-bootstrap.py
--
-- Provides Supabase-shaped auth/storage stubs and legacy public tables that
-- existed before DbUp. Esportra.Migrator incremental scripts ALTER these objects.

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- ── Roles used by RLS policies ─────────────────────────────────────────────
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
    CREATE ROLE anon NOLOGIN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
    CREATE ROLE authenticated NOLOGIN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN
    CREATE ROLE service_role NOLOGIN BYPASSRLS;
  END IF;
END $$;

-- ── Auth schema (minimal Supabase stub) ────────────────────────────────────
CREATE SCHEMA IF NOT EXISTS auth;

CREATE TABLE IF NOT EXISTS auth.users (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  email TEXT,
  raw_user_meta_data JSONB NOT NULL DEFAULT '{{}}'::jsonb
);

CREATE OR REPLACE FUNCTION auth.uid()
RETURNS UUID
LANGUAGE sql
STABLE
AS $$
  SELECT NULLIF(current_setting('request.jwt.claim.sub', true), '')::uuid;
$$;

CREATE OR REPLACE FUNCTION auth.role()
RETURNS TEXT
LANGUAGE sql
STABLE
AS $$
  SELECT COALESCE(current_setting('request.jwt.claim.role', true), 'anon');
$$;

CREATE OR REPLACE FUNCTION auth.jwt()
RETURNS JSONB
LANGUAGE sql
STABLE
AS $$
  SELECT '{{}}'::jsonb;
$$;

-- ── Storage schema (minimal Supabase stub) ─────────────────────────────────
CREATE SCHEMA IF NOT EXISTS storage;

CREATE TABLE IF NOT EXISTS storage.buckets (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  public BOOLEAN NOT NULL DEFAULT false,
  file_size_limit BIGINT,
  allowed_mime_types TEXT[]
);

CREATE TABLE IF NOT EXISTS storage.objects (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  bucket_id TEXT REFERENCES storage.buckets(id),
  name TEXT,
  owner UUID,
  metadata JSONB NOT NULL DEFAULT '{{}}'::jsonb
);

-- ── Common enums (migrations cast to these) ────────────────────────────────
DO $$ BEGIN
  CREATE TYPE public.app_role AS ENUM (
    'casual', 'organizer', 'admin', 'venue_owner', 'partner', 'player'
  );
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE TYPE public.tournament_status AS ENUM (
    'draft', 'pending', 'pending_approval', 'approved', 'published', 'open',
    'closed', 'check_in', 'ongoing', 'completed', 'cancelled'
  );
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE TYPE public.team_member_role AS ENUM (
    'owner', 'captain', 'member', 'coach', 'substitute'
  );
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE TYPE public.notification_type AS ENUM (
    'system', 'tournament', 'match', 'team', 'dispute', 'payment',
    'match_ready', 'br_round', 'invite', 'admin'
  );
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

-- ── Profiles (auth trigger + most FK targets) ──────────────────────────────
CREATE TABLE IF NOT EXISTS public.profiles (
  id UUID PRIMARY KEY,
  username TEXT,
  full_name TEXT,
  email TEXT,
  avatar_url TEXT,
  role public.app_role DEFAULT 'casual',
  date_of_birth DATE,
  is_admin BOOLEAN NOT NULL DEFAULT false,
  riot_tag TEXT,
  steam_tag TEXT,
  faceit_nickname TEXT,
  settings JSONB NOT NULL DEFAULT '{{}}'::jsonb,
  social_links JSONB NOT NULL DEFAULT '{{}}'::jsonb
);

-- ── Legacy public tables (id stub; migrations add columns) ─────────────────
{table_ddl}

-- team_invitations may pre-exist on some DBs; ensure shell exists before RLS fixes
CREATE TABLE IF NOT EXISTS public.team_invitations (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid()
);

CREATE TABLE IF NOT EXISTS public.team_rosters (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  team_id UUID
);

CREATE TABLE IF NOT EXISTS public.team_roster_members (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  roster_id UUID,
  user_id UUID
);

SELECT 'replay bootstrap applied' AS status;
"""


def main() -> int:
    if not MIGRATIONS_DIR.is_dir():
        print(f"Missing migrations dir: {MIGRATIONS_DIR}", file=sys.stderr)
        return 1

    files = sorted(MIGRATIONS_DIR.glob("*.sql"), key=lambda p: p.name)
    defined = collect_defined_tables(files)
    referenced = collect_referenced_public_tables(files)
    legacy = referenced - defined

    # profiles gets full DDL above; remove from stub list if present
    legacy.discard("profiles")
    legacy.discard("team_invitations")
    legacy.discard("team_rosters")
    legacy.discard("team_roster_members")

    sql = render_bootstrap(legacy)
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(sql, encoding="utf-8")
    print(f"Wrote {OUTPUT} ({len(legacy)} legacy table stubs + auth/storage/enums)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
