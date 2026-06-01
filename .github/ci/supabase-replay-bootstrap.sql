-- CI migration replay bootstrap (do NOT apply in production)
-- Used only as input when building post-baseline-replay-schema.sql (see
-- .github/scripts/generate-post-baseline-replay-schema.py or build-post-baseline-schema.sh).
-- CI replay applies post-baseline-replay-schema.sql + replay-journal-seed.sql instead.
--
-- Regenerate table stubs: python .github/scripts/generate-replay-bootstrap.py

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
  raw_user_meta_data JSONB NOT NULL DEFAULT '{}'::jsonb
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
  SELECT '{}'::jsonb;
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
  metadata JSONB NOT NULL DEFAULT '{}'::jsonb
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
  admin_roles TEXT[] NOT NULL DEFAULT '{}',
  riot_tag TEXT,
  steam_tag TEXT,
  faceit_nickname TEXT,
  settings JSONB NOT NULL DEFAULT '{}'::jsonb,
  social_links JSONB NOT NULL DEFAULT '{}'::jsonb
);

-- ── Legacy public tables (stubs; migrations add/alter columns) ─────────────
CREATE TABLE IF NOT EXISTS public.audit_logs (id UUID PRIMARY KEY DEFAULT gen_random_uuid());
CREATE TABLE IF NOT EXISTS public.brkt_versions (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tournament_id UUID);
CREATE TABLE IF NOT EXISTS public.brkt_match_games (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), match_id UUID);
CREATE TABLE IF NOT EXISTS public.brkt_matches (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  version_id UUID,
  team1_id UUID,
  team2_id UUID
);
CREATE TABLE IF NOT EXISTS public.dispute_comments (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), dispute_id UUID);
CREATE TABLE IF NOT EXISTS public.match_map_veto_actions (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), veto_id UUID);
CREATE TABLE IF NOT EXISTS public.match_map_vetos (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), match_id UUID);
CREATE TABLE IF NOT EXISTS public.match_result_reports (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), match_id UUID);
CREATE TABLE IF NOT EXISTS public.notifications (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), user_id UUID);
CREATE TABLE IF NOT EXISTS public.organizations (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), owner_id UUID);
CREATE TABLE IF NOT EXISTS public.sponsor_impressions (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), sponsor_id UUID);
CREATE TABLE IF NOT EXISTS public.sponsors (id UUID PRIMARY KEY DEFAULT gen_random_uuid());
CREATE TABLE IF NOT EXISTS public.staff_audit_log (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), organization_id UUID);
CREATE TABLE IF NOT EXISTS public.team_invitations (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  team_id UUID,
  invited_user_id UUID
);
CREATE TABLE IF NOT EXISTS public.team_members (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  team_id UUID,
  user_id UUID,
  role public.team_member_role DEFAULT 'member'
);
CREATE TABLE IF NOT EXISTS public.team_roster_members (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), roster_id UUID, user_id UUID);
CREATE TABLE IF NOT EXISTS public.team_rosters (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), team_id UUID);
CREATE TABLE IF NOT EXISTS public.teams (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), owner_id UUID);
CREATE TABLE IF NOT EXISTS public.tournament_disputes (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tournament_id UUID);
CREATE TABLE IF NOT EXISTS public.tournament_participants (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tournament_id UUID,
  team_id UUID,
  user_id UUID
);
CREATE TABLE IF NOT EXISTS public.tournament_stages (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tournament_id UUID);
CREATE TABLE IF NOT EXISTS public.tournaments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  organizer_id UUID,
  status public.tournament_status DEFAULT 'draft',
  is_featured BOOLEAN NOT NULL DEFAULT false,
  approved_by UUID,
  approved_at TIMESTAMPTZ,
  winner_id UUID
);
CREATE TABLE IF NOT EXISTS public.user_roles (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), user_id UUID);
CREATE TABLE IF NOT EXISTS public.venue_bookings (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), venue_id UUID);
CREATE TABLE IF NOT EXISTS public.verified_roles (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), user_id UUID);
CREATE TABLE IF NOT EXISTS public.venues (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_id UUID,
  is_active BOOLEAN NOT NULL DEFAULT false,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Patch columns when an older bootstrap stub already created the shell table.
ALTER TABLE public.team_members ADD COLUMN IF NOT EXISTS role public.team_member_role DEFAULT 'member';
ALTER TABLE public.team_invitations ADD COLUMN IF NOT EXISTS invited_user_id UUID;
ALTER TABLE public.brkt_matches ADD COLUMN IF NOT EXISTS version_id UUID;
ALTER TABLE public.brkt_matches ADD COLUMN IF NOT EXISTS team1_id UUID;
ALTER TABLE public.brkt_matches ADD COLUMN IF NOT EXISTS team2_id UUID;
ALTER TABLE public.tournaments ADD COLUMN IF NOT EXISTS is_featured BOOLEAN NOT NULL DEFAULT false;
ALTER TABLE public.tournaments ADD COLUMN IF NOT EXISTS approved_by UUID;
ALTER TABLE public.tournaments ADD COLUMN IF NOT EXISTS approved_at TIMESTAMPTZ;
ALTER TABLE public.tournaments ADD COLUMN IF NOT EXISTS winner_id UUID;
ALTER TABLE public.profiles ADD COLUMN IF NOT EXISTS admin_roles TEXT[] NOT NULL DEFAULT '{}';
ALTER TABLE public.venues ADD COLUMN IF NOT EXISTS is_active BOOLEAN NOT NULL DEFAULT false;
ALTER TABLE public.venues ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT now();
ALTER TABLE public.team_members ADD COLUMN IF NOT EXISTS is_active BOOLEAN NOT NULL DEFAULT true;

SELECT 'replay bootstrap applied' AS status;
