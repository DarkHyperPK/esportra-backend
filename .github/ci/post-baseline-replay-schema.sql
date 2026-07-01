-- CI post-baseline replay schema snapshot (generated — do not apply in production)
-- Regenerate: python .github/scripts/generate-post-baseline-replay-schema.py
-- Or with Postgres: bash .github/scripts/build-post-baseline-schema.sh

-- ── Supabase stub (legacy schema shells) ────────────────────────────────
-- CI migration replay bootstrap (do NOT apply in production)
-- Used only as input when building post-baseline-replay-schema.sql (see
-- .github/scripts/generate-post-baseline-replay-schema.py or build-post-baseline-schema.sh).
-- CI replay applies post-baseline-replay-schema.sql + replay-journal-seed.sql instead.
--
-- Supabase/platform stubs (auth, storage, roles, enums) live here.
-- Rich legacy public app DDL belongs in replay-legacy-overlays.sql.
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
  social_links JSONB NOT NULL DEFAULT '{}'::jsonb,
  license_id UUID
);

-- Supabase Realtime publication (referenced by pre-baseline REPLICA migrations)
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'supabase_realtime') THEN
    CREATE PUBLICATION supabase_realtime;
  END IF;
END $$;

-- Legacy RPC stubs referenced before they are redefined in later migrations
CREATE OR REPLACE FUNCTION public.proc_internal_advance_match(
  p_match_id UUID, p_winner_id UUID, p_loser_id UUID
)
RETURNS VOID
LANGUAGE plpgsql
AS $$ BEGIN NULL; END; $$;

-- ── Legacy public tables (stubs; migrations add/alter columns) ─────────────
CREATE TABLE IF NOT EXISTS public.admin_roles (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL UNIQUE,
  key TEXT UNIQUE,
  description TEXT,
  created_at TIMESTAMPTZ DEFAULT now()
);
CREATE TABLE IF NOT EXISTS public.admin_user_roles (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
  role_id UUID NOT NULL REFERENCES public.admin_roles(id) ON DELETE CASCADE,
  assigned_by UUID REFERENCES auth.users(id),
  assigned_at TIMESTAMPTZ DEFAULT now(),
  UNIQUE (user_id, role_id)
);
CREATE TABLE IF NOT EXISTS public.audit_logs (id UUID PRIMARY KEY DEFAULT gen_random_uuid());
CREATE TABLE IF NOT EXISTS public.brkt_versions (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), tournament_id UUID);
CREATE TABLE IF NOT EXISTS public.brkt_match_games (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), match_id UUID);
CREATE TABLE IF NOT EXISTS public.brkt_matches (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  version_id UUID,
  team1_id UUID,
  team2_id UUID,
  version INTEGER NOT NULL DEFAULT 1,
  winner_id UUID,
  loser_id UUID,
  team1_score INTEGER,
  team2_score INTEGER,
  status TEXT DEFAULT 'pending',
  updated_at TIMESTAMPTZ DEFAULT now()
);
CREATE TABLE IF NOT EXISTS public.dispute_comments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  dispute_id UUID,
  user_id UUID,
  is_internal BOOLEAN NOT NULL DEFAULT false
);
CREATE TABLE IF NOT EXISTS public.match_map_veto_actions (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), veto_id UUID);
CREATE TABLE IF NOT EXISTS public.match_map_vetos (id UUID PRIMARY KEY DEFAULT gen_random_uuid(), match_id UUID);
CREATE TABLE IF NOT EXISTS public.match_result_reports (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  match_id UUID,
  reported_by UUID,
  status TEXT DEFAULT 'pending',
  responded_by UUID,
  responded_at TIMESTAMPTZ,
  dispute_reason TEXT
);
CREATE TABLE IF NOT EXISTS public.match_completed_events (
  match_id UUID,
  winner_id UUID,
  loser_id UUID,
  status TEXT
);
CREATE TABLE IF NOT EXISTS public.notifications (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID,
  type public.notification_type DEFAULT 'system',
  title TEXT,
  message TEXT,
  link TEXT,
  data JSONB NOT NULL DEFAULT '{}'::jsonb,
  is_read BOOLEAN NOT NULL DEFAULT false
);
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
CREATE TABLE IF NOT EXISTS public.tournament_disputes (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tournament_id UUID,
  match_id UUID,
  raised_by_user_id UUID,
  assigned_to_user_id UUID,
  team_id UUID,
  title TEXT,
  description TEXT,
  status TEXT,
  dispute_reason TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS public.tournament_staff (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tournament_id UUID,
  user_id UUID,
  status TEXT DEFAULT 'active',
  permissions TEXT[] NOT NULL DEFAULT '{}'
);
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
  organization_id UUID,
  slug TEXT,
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
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  latitude DOUBLE PRECISION,
  longitude DOUBLE PRECISION
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
ALTER TABLE public.profiles ADD COLUMN IF NOT EXISTS license_id UUID;
ALTER TABLE public.venues ADD COLUMN IF NOT EXISTS is_active BOOLEAN NOT NULL DEFAULT false;
ALTER TABLE public.venues ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT now();
ALTER TABLE public.venues ADD COLUMN IF NOT EXISTS latitude DOUBLE PRECISION;
ALTER TABLE public.venues ADD COLUMN IF NOT EXISTS longitude DOUBLE PRECISION;
ALTER TABLE public.team_members ADD COLUMN IF NOT EXISTS is_active BOOLEAN NOT NULL DEFAULT true;
ALTER TABLE public.tournaments ADD COLUMN IF NOT EXISTS organization_id UUID;
ALTER TABLE public.tournaments ADD COLUMN IF NOT EXISTS slug TEXT;
ALTER TABLE public.brkt_matches ADD COLUMN IF NOT EXISTS version INTEGER NOT NULL DEFAULT 1;
ALTER TABLE public.brkt_matches ADD COLUMN IF NOT EXISTS winner_id UUID;
ALTER TABLE public.brkt_matches ADD COLUMN IF NOT EXISTS loser_id UUID;
ALTER TABLE public.brkt_matches ADD COLUMN IF NOT EXISTS team1_score INTEGER;
ALTER TABLE public.brkt_matches ADD COLUMN IF NOT EXISTS team2_score INTEGER;
ALTER TABLE public.brkt_matches ADD COLUMN IF NOT EXISTS status TEXT DEFAULT 'pending';
ALTER TABLE public.brkt_matches ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ DEFAULT now();
ALTER TABLE public.tournament_disputes ADD COLUMN IF NOT EXISTS match_id UUID;
ALTER TABLE public.tournament_disputes ADD COLUMN IF NOT EXISTS raised_by_user_id UUID;
ALTER TABLE public.tournament_disputes ADD COLUMN IF NOT EXISTS assigned_to_user_id UUID;
ALTER TABLE public.tournament_disputes ADD COLUMN IF NOT EXISTS team_id UUID;
ALTER TABLE public.tournament_disputes ADD COLUMN IF NOT EXISTS title TEXT;
ALTER TABLE public.tournament_disputes ADD COLUMN IF NOT EXISTS description TEXT;
ALTER TABLE public.tournament_disputes ADD COLUMN IF NOT EXISTS status TEXT;
ALTER TABLE public.tournament_disputes ADD COLUMN IF NOT EXISTS dispute_reason TEXT;
ALTER TABLE public.tournament_disputes ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT now();
ALTER TABLE public.match_result_reports ADD COLUMN IF NOT EXISTS reported_by UUID;
ALTER TABLE public.match_result_reports ADD COLUMN IF NOT EXISTS status TEXT DEFAULT 'pending';
ALTER TABLE public.match_result_reports ADD COLUMN IF NOT EXISTS responded_by UUID;
ALTER TABLE public.match_result_reports ADD COLUMN IF NOT EXISTS responded_at TIMESTAMPTZ;
ALTER TABLE public.match_result_reports ADD COLUMN IF NOT EXISTS dispute_reason TEXT;
ALTER TABLE public.dispute_comments ADD COLUMN IF NOT EXISTS is_internal BOOLEAN NOT NULL DEFAULT false;
ALTER TABLE public.notifications ADD COLUMN IF NOT EXISTS type public.notification_type DEFAULT 'system';
ALTER TABLE public.notifications ADD COLUMN IF NOT EXISTS title TEXT;
ALTER TABLE public.notifications ADD COLUMN IF NOT EXISTS message TEXT;
ALTER TABLE public.notifications ADD COLUMN IF NOT EXISTS link TEXT;
ALTER TABLE public.notifications ADD COLUMN IF NOT EXISTS data JSONB NOT NULL DEFAULT '{}'::jsonb;
ALTER TABLE public.notifications ADD COLUMN IF NOT EXISTS is_read BOOLEAN NOT NULL DEFAULT false;

SELECT 'replay bootstrap applied' AS status;

-- ── Legacy overlays (tables never CREATE TABLE'd in Scripts/) ─────────────
-- CI post-baseline replay legacy overlays (do NOT apply in production)
-- Curated public schema objects assumed by post-baseline migrations but never
-- CREATE TABLE'd in Scripts/. Included in post-baseline-replay-schema.sql.
-- Regenerate snapshot: python .github/scripts/generate-post-baseline-replay-schema.py

-- ── Supabase extensions schema (pgcrypto in extensions, not public) ─────────
CREATE SCHEMA IF NOT EXISTS extensions;

DO $$
BEGIN
  IF EXISTS (
    SELECT 1
    FROM pg_extension e
    JOIN pg_namespace n ON n.oid = e.extnamespace
    WHERE e.extname = 'pgcrypto' AND n.nspname = 'public'
  ) THEN
    ALTER EXTENSION pgcrypto SET SCHEMA extensions;
  ELSIF NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pgcrypto') THEN
    CREATE EXTENSION pgcrypto WITH SCHEMA extensions;
  END IF;
END $$;

CREATE EXTENSION IF NOT EXISTS "uuid-ossp" WITH SCHEMA extensions;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE p.proname = 'uuid_generate_v4' AND n.nspname = 'public'
  ) THEN
    CREATE FUNCTION public.uuid_generate_v4() RETURNS uuid
      LANGUAGE sql VOLATILE
      AS $fn$ SELECT extensions.uuid_generate_v4() $fn$;
  END IF;
END $$;

CREATE OR REPLACE FUNCTION public.update_updated_at_column()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $fn$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$fn$;

-- ── RBAC (20260323_001_seed_rbac_normalized.sql) ───────────────────────────
CREATE TABLE IF NOT EXISTS public.admin_permissions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL UNIQUE,
  description TEXT,
  resource TEXT,
  action TEXT
);

CREATE TABLE IF NOT EXISTS public.admin_role_permissions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  role_id UUID NOT NULL REFERENCES public.admin_roles(id) ON DELETE CASCADE,
  permission_id UUID NOT NULL REFERENCES public.admin_permissions(id) ON DELETE CASCADE
);

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'admin_role_permissions_role_id_permission_id_key'
  ) THEN
    ALTER TABLE public.admin_role_permissions
      ADD CONSTRAINT admin_role_permissions_role_id_permission_id_key
      UNIQUE (role_id, permission_id);
  END IF;
END $$;

-- ── Organization staff (20260325140000, 20260325150000, 20260702100000) ────
CREATE TABLE IF NOT EXISTS public.organization_staff (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  organization_id UUID,
  user_id UUID,
  role TEXT NOT NULL DEFAULT 'staff' CHECK (role IN ('owner', 'admin', 'staff')),
  status TEXT DEFAULT 'pending',
  permissions TEXT[] NOT NULL DEFAULT '{}',
  accepted_at TIMESTAMPTZ,
  updated_at TIMESTAMPTZ DEFAULT now()
);

CREATE TABLE IF NOT EXISTS public.staff_tournament_assignments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  organization_staff_id UUID REFERENCES public.organization_staff(id) ON DELETE CASCADE,
  tournament_id UUID
);

-- ── Game maps seeds (20260326150000+) ──────────────────────────────────────
CREATE TABLE IF NOT EXISTS public.game_maps (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  game TEXT NOT NULL,
  map_name TEXT NOT NULL,
  is_active BOOLEAN NOT NULL DEFAULT true
);

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'game_maps_game_map_name_key'
  ) THEN
    ALTER TABLE public.game_maps
      ADD CONSTRAINT game_maps_game_map_name_key UNIQUE (game, map_name);
  END IF;
END $$;

-- ── Column overlays on bootstrap stubs ─────────────────────────────────────
ALTER TABLE public.venues
  ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS status TEXT;

ALTER TABLE public.sponsor_impressions
  ADD COLUMN IF NOT EXISTS event_type TEXT,
  ADD COLUMN IF NOT EXISTS visitor_id TEXT,
  ADD COLUMN IF NOT EXISTS tournament_id UUID;

ALTER TABLE public.tournaments
  ADD COLUMN IF NOT EXISTS game TEXT;

ALTER TABLE public.user_roles
  ADD COLUMN IF NOT EXISTS role TEXT;

-- verified_roles.role uses app_role enum (matches profiles.role)
ALTER TABLE public.verified_roles
  ADD COLUMN IF NOT EXISTS role public.app_role;

ALTER TABLE public.tournament_participants
  ADD COLUMN IF NOT EXISTS participant_type TEXT,
  ADD COLUMN IF NOT EXISTS status TEXT;

ALTER TABLE public.teams
  ADD COLUMN IF NOT EXISTS name TEXT,
  ADD COLUMN IF NOT EXISTS tag TEXT,
  ADD COLUMN IF NOT EXISTS game TEXT,
  ADD COLUMN IF NOT EXISTS game_format TEXT,
  ADD COLUMN IF NOT EXISTS is_solo BOOLEAN NOT NULL DEFAULT false,
  ADD COLUMN IF NOT EXISTS max_members INTEGER,
  ADD COLUMN IF NOT EXISTS logo_url TEXT;

ALTER TABLE public.profiles
  ADD COLUMN IF NOT EXISTS username TEXT,
  ADD COLUMN IF NOT EXISTS avatar_url TEXT;

ALTER TABLE public.audit_logs
  ADD COLUMN IF NOT EXISTS target_type TEXT,
  ADD COLUMN IF NOT EXISTS target_id UUID,
  ADD COLUMN IF NOT EXISTS action_type TEXT,
  ADD COLUMN IF NOT EXISTS admin_id UUID,
  ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ DEFAULT now();

SELECT 'replay legacy overlays applied' AS status;

-- ── 20260228233709_fix_tournament_status_trigger.sql ────────────────────────────────────────────────────────
-- Fix block_organizer_sensitive_tournament_updates trigger
-- The trigger referenced 'pending_approval' in an enum comparison, but that value
-- was never added to the tournament_status enum. Any status change by an organizer
-- caused PostgreSQL to fail the implicit enum cast:
--   "invalid input value for enum tournament_status: 'pending_approval'"
-- Fix: cast OLD.status to text before comparing, so the literal string comparison
-- is safe regardless of what values exist in the enum.

CREATE OR REPLACE FUNCTION public.block_organizer_sensitive_tournament_updates()
  RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
AS $function$
BEGIN
  -- Allow service_role and admins to update anything
  IF current_setting('request.jwt.claim.role', true) = 'service_role' THEN
    RETURN NEW;
  END IF;

  IF EXISTS (SELECT 1 FROM profiles WHERE id = auth.uid() AND is_admin = true) THEN
    RETURN NEW;
  END IF;

  -- Block organizers from updating these sensitive columns
  IF NEW.is_featured IS DISTINCT FROM OLD.is_featured THEN
    RAISE EXCEPTION 'Only admins can change is_featured';
  END IF;

  -- Cast to text before comparing so this is safe even if the enum value
  -- does not currently exist (avoids implicit cast failure).
  IF NEW.status IS DISTINCT FROM OLD.status AND OLD.status::text = 'pending_approval' THEN
    RAISE EXCEPTION 'Only admins can approve tournaments';
  END IF;

  IF NEW.approved_by IS DISTINCT FROM OLD.approved_by THEN
    RAISE EXCEPTION 'Only admins can set approved_by';
  END IF;

  IF NEW.approved_at IS DISTINCT FROM OLD.approved_at THEN
    RAISE EXCEPTION 'Only admins can set approved_at';
  END IF;

  IF NEW.winner_id IS DISTINCT FROM OLD.winner_id THEN
    RAISE EXCEPTION 'Only admins can set winner_id';
  END IF;

  RETURN NEW;
END;
$function$;

-- ── 20260301000000_restore_handle_new_user_trigger.sql ────────────────────────────────────────────────────────
-- Recreate the handle_new_user trigger that auto-creates a profile row
-- whenever a new user is inserted into auth.users.
-- This trigger was lost during prod→staging syncs because pg_dump does not
-- include triggers on auth schema tables. Adding it here ensures it is
-- re-applied on both staging and production via the migration pipeline.

CREATE OR REPLACE FUNCTION public.handle_new_user()
  RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path TO 'public'
AS $function$
BEGIN
    INSERT INTO public.profiles (id, username, full_name, email, avatar_url, role, date_of_birth)
    VALUES (
        NEW.id,
        COALESCE(NEW.raw_user_meta_data->>'username', split_part(NEW.email, '@', 1)),
        COALESCE(NEW.raw_user_meta_data->>'full_name', split_part(NEW.email, '@', 1)),
        NEW.email,
        NEW.raw_user_meta_data->>'avatar_url',
        COALESCE(NEW.raw_user_meta_data->>'role', 'casual')::app_role,
        (NEW.raw_user_meta_data->>'date_of_birth')::date
    );
    RETURN NEW;
END;
$function$;

-- Drop first to avoid "trigger already exists" error on re-runs
DROP TRIGGER IF EXISTS on_auth_user_created ON auth.users;

CREATE TRIGGER on_auth_user_created
  AFTER INSERT ON auth.users
  FOR EACH ROW EXECUTE FUNCTION public.handle_new_user();

-- ── 20260301000001_fix_team_invitations_rls.sql ────────────────────────────────────────────────────────
-- Fix team_invitations RLS policies to allow captains/owners to send and view invites.
-- Previously, INSERT and SELECT were restricted to team owner only, blocking captains
-- from sending roster invites. The DELETE policy already allows captains — this brings
-- INSERT and SELECT into alignment.

-- Fix INSERT: allow team owner OR team member with captain/owner role
DROP POLICY IF EXISTS team_invitations_insert_policy ON public.team_invitations;
CREATE POLICY team_invitations_insert_policy ON public.team_invitations
  FOR INSERT WITH CHECK (
    (
      EXISTS (
        SELECT 1 FROM public.teams t
        WHERE t.id = team_invitations.team_id
          AND t.owner_id = ( SELECT auth.uid() AS uid)
      )
    ) OR (
      EXISTS (
        SELECT 1 FROM public.team_members tm
        WHERE tm.team_id = team_invitations.team_id
          AND tm.user_id = ( SELECT auth.uid() AS uid)
          AND (tm.role = 'captain'::public.team_member_role OR tm.role = 'owner'::public.team_member_role)
      )
    ) OR (
      ( SELECT auth.role() AS role) = 'service_role'::text
    )
  );

-- Fix SELECT: allow invited user, team owner, team captain/owner, or service_role
DROP POLICY IF EXISTS team_invitations_select_policy ON public.team_invitations;
CREATE POLICY team_invitations_select_policy ON public.team_invitations
  FOR SELECT USING (
    (
      invited_user_id = ( SELECT auth.uid() AS uid)
    ) OR (
      EXISTS (
        SELECT 1 FROM public.teams t
        WHERE t.id = team_invitations.team_id
          AND t.owner_id = ( SELECT auth.uid() AS uid)
      )
    ) OR (
      EXISTS (
        SELECT 1 FROM public.team_members tm
        WHERE tm.team_id = team_invitations.team_id
          AND tm.user_id = ( SELECT auth.uid() AS uid)
          AND (tm.role = 'captain'::public.team_member_role OR tm.role = 'owner'::public.team_member_role)
      )
    ) OR (
      ( SELECT auth.role() AS role) = 'service_role'::text
    )
  );

-- ── 20260302000000_add_faceit_accounts.sql ────────────────────────────────────────────────────────
-- Add faceit_nickname to profiles (mirrors riot_tag pattern)
ALTER TABLE public.profiles ADD COLUMN IF NOT EXISTS faceit_nickname text;

-- Create faceit_accounts table (mirrors riot_accounts structure)
CREATE TABLE IF NOT EXISTS public.faceit_accounts (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    faceit_id text NOT NULL,
    nickname text NOT NULL,
    avatar_url text,
    access_token text,
    refresh_token text,
    token_expires_at timestamp with time zone,
    linked_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    CONSTRAINT faceit_accounts_pkey PRIMARY KEY (id),
    CONSTRAINT faceit_accounts_user_id_key UNIQUE (user_id),
    CONSTRAINT faceit_accounts_faceit_id_key UNIQUE (faceit_id),
    CONSTRAINT faceit_accounts_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.profiles(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_faceit_accounts_user_id ON public.faceit_accounts(user_id);
CREATE INDEX IF NOT EXISTS idx_faceit_accounts_faceit_id ON public.faceit_accounts(faceit_id);

-- RLS
ALTER TABLE public.faceit_accounts ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS faceit_accounts_select_policy ON public.faceit_accounts;
CREATE POLICY faceit_accounts_select_policy ON public.faceit_accounts
    FOR SELECT USING (
        user_id = ( SELECT auth.uid() AS uid)
        OR ( SELECT auth.role() AS role) = 'service_role'::text
    );

DROP POLICY IF EXISTS faceit_accounts_delete_policy ON public.faceit_accounts;
CREATE POLICY faceit_accounts_delete_policy ON public.faceit_accounts
    FOR DELETE USING (
        user_id = ( SELECT auth.uid() AS uid)
        OR ( SELECT auth.role() AS role) = 'service_role'::text
    );

-- Update get_roster_members RPC to expose faceit_nickname alongside riot_tag
DROP FUNCTION IF EXISTS public.get_roster_members(uuid);
CREATE OR REPLACE FUNCTION public.get_roster_members(r_id uuid)
RETURNS TABLE(user_id uuid, username text, full_name text, riot_tag text, steam_tag text, faceit_nickname text)
LANGUAGE plpgsql
AS $$
declare
  t_id uuid;
begin
  select team_id into t_id from team_rosters where id = r_id;

  return query
  with roster_members as (
    select m.user_id, p.username, p.full_name, p.riot_tag, p.steam_tag, p.faceit_nickname
    from team_roster_members m
    join profiles p on p.id = m.user_id
    where m.roster_id = r_id
  ),
  team_owner as (
    select t.owner_id as user_id, p.username, p.full_name, p.riot_tag, p.steam_tag, p.faceit_nickname
    from teams t
    join profiles p on p.id = t.owner_id
    where t.id = t_id
  )
  select user_id, username, full_name, riot_tag, steam_tag, faceit_nickname from roster_members
  union
  select user_id, username, full_name, riot_tag, steam_tag, faceit_nickname from team_owner;
end;
$$;

-- ── 20260303100000_venue_overhaul.sql ────────────────────────────────────────────────────────
-- ============================================================
-- Venue Management Overhaul
-- Adds: venue_id (VEN-XXXXXX), status workflow, price_per_hour,
--       subscription_tier, desktop_pairing_token
-- ============================================================

-- 1. Add new columns to venues
ALTER TABLE public.venues
  ADD COLUMN IF NOT EXISTS venue_id TEXT UNIQUE,
  ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'draft'
    CONSTRAINT venues_status_check CHECK (
      status IN ('draft','pending_review','published','rejected','suspended','archived')
    ),
  ADD COLUMN IF NOT EXISTS rejection_reason TEXT,
  ADD COLUMN IF NOT EXISTS reviewed_by UUID REFERENCES public.profiles(id),
  ADD COLUMN IF NOT EXISTS reviewed_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS submitted_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS published_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS subscription_tier TEXT NOT NULL DEFAULT 'free'
    CONSTRAINT venues_tier_check CHECK (
      subscription_tier IN ('free','basic','pro','enterprise')
    ),
  ADD COLUMN IF NOT EXISTS desktop_pairing_token TEXT UNIQUE,
  ADD COLUMN IF NOT EXISTS price_per_hour NUMERIC(10,2) DEFAULT 0;

-- 2. Auto-generate VEN-XXXXXX on INSERT (6-digit zero-padded random)
CREATE OR REPLACE FUNCTION generate_venue_id()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE
  candidate TEXT;
BEGIN
  IF NEW.venue_id IS NULL THEN
    LOOP
      candidate := 'VEN-' || LPAD((FLOOR(RANDOM() * 900000) + 100000)::TEXT, 6, '0');
      EXIT WHEN NOT EXISTS (SELECT 1 FROM public.venues WHERE venue_id = candidate);
    END LOOP;
    NEW.venue_id := candidate;
  END IF;
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS set_venue_id ON public.venues;
CREATE TRIGGER set_venue_id
  BEFORE INSERT ON public.venues
  FOR EACH ROW EXECUTE FUNCTION generate_venue_id();

-- 3. Auto-generate 6-char alphanumeric pairing token on INSERT
--    Excludes ambiguous chars (0, O, I, 1)
CREATE OR REPLACE FUNCTION generate_pairing_token()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE
  chars TEXT := 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  candidate TEXT;
  i INTEGER;
BEGIN
  IF NEW.desktop_pairing_token IS NULL THEN
    LOOP
      candidate := '';
      FOR i IN 1..6 LOOP
        candidate := candidate || SUBSTR(chars, FLOOR(RANDOM() * LENGTH(chars) + 1)::INT, 1);
      END LOOP;
      EXIT WHEN NOT EXISTS (SELECT 1 FROM public.venues WHERE desktop_pairing_token = candidate);
    END LOOP;
    NEW.desktop_pairing_token := candidate;
  END IF;
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS set_pairing_token ON public.venues;
CREATE TRIGGER set_pairing_token
  BEFORE INSERT ON public.venues
  FOR EACH ROW EXECUTE FUNCTION generate_pairing_token();

-- 4. Backfill existing venues: give them IDs, tokens, mark as published
DO $$
DECLARE
  v RECORD;
  vid TEXT;
  ptok TEXT;
  chars TEXT := 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  i INT;
BEGIN
  FOR v IN SELECT id FROM public.venues WHERE venue_id IS NULL LOOP
    -- Generate unique venue_id
    LOOP
      vid := 'VEN-' || LPAD((FLOOR(RANDOM() * 900000) + 100000)::TEXT, 6, '0');
      EXIT WHEN NOT EXISTS (SELECT 1 FROM public.venues WHERE venue_id = vid);
    END LOOP;
    -- Generate unique pairing token
    LOOP
      ptok := '';
      FOR i IN 1..6 LOOP
        ptok := ptok || SUBSTR(chars, FLOOR(RANDOM() * LENGTH(chars) + 1)::INT, 1);
      END LOOP;
      EXIT WHEN NOT EXISTS (SELECT 1 FROM public.venues WHERE desktop_pairing_token = ptok);
    END LOOP;

    UPDATE public.venues
    SET
      venue_id = vid,
      desktop_pairing_token = ptok,
      status = CASE WHEN is_active = true THEN 'published' ELSE 'draft' END,
      published_at = CASE WHEN is_active = true THEN created_at ELSE NULL END
    WHERE id = v.id;
  END LOOP;
END;
$$;

-- 5. RLS: update SELECT policy so public only sees published venues
--    Owners see their own (any status). Admins see all.
ALTER TABLE public.venues ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS "Published venues are viewable by everyone" ON public.venues;
CREATE POLICY "Published venues are viewable by everyone"
  ON public.venues FOR SELECT
  USING (
    status = 'published'
    OR (auth.uid() IS NOT NULL AND owner_id = auth.uid())
    OR EXISTS (
      SELECT 1 FROM public.profiles
      WHERE id = auth.uid()
        AND (is_admin = true OR 'venue_admin' = ANY(admin_roles::text[]))
    )
  );

-- 6. Allow admins to update status, reviewed_by, rejection_reason etc.
DROP POLICY IF EXISTS "Admins can update any venue" ON public.venues;
CREATE POLICY "Admins can update any venue"
  ON public.venues FOR UPDATE
  USING (
    EXISTS (
      SELECT 1 FROM public.profiles
      WHERE id = auth.uid()
        AND (is_admin = true OR 'venue_admin' = ANY(admin_roles::text[]))
    )
  );

-- Allow venue owners to update their own venues (but not status to 'published' directly)
DROP POLICY IF EXISTS "Owners can update their own venues" ON public.venues;
CREATE POLICY "Owners can update their own venues"
  ON public.venues FOR UPDATE
  USING (auth.uid() IS NOT NULL AND owner_id = auth.uid())
  WITH CHECK (
    -- Owners cannot self-approve; they can only set draft/pending_review
    status IN ('draft', 'pending_review')
  );

-- ── 20260303110000_license_system.sql ────────────────────────────────────────────────────────
-- License system: ESP-VO-XXXXXX / ESP-OR-XXXXXX / ESP-BC-XXXXXX identifiers
CREATE TABLE IF NOT EXISTS public.licenses (
  id UUID DEFAULT gen_random_uuid() PRIMARY KEY,
  user_id UUID NOT NULL REFERENCES public.profiles(id) ON DELETE CASCADE,
  license_id TEXT UNIQUE NOT NULL DEFAULT '',
  license_type TEXT NOT NULL
    CONSTRAINT licenses_type_check CHECK (license_type IN ('venue_owner','organizer','broadcaster')),
  status TEXT NOT NULL DEFAULT 'active'
    CONSTRAINT licenses_status_check CHECK (status IN ('active','suspended','revoked')),
  issued_at TIMESTAMPTZ DEFAULT NOW(),
  expires_at TIMESTAMPTZ,
  notes TEXT,
  created_at TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE public.licenses ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Users see own licenses"
  ON public.licenses FOR SELECT
  USING (user_id = auth.uid());

CREATE POLICY "Admins manage all licenses"
  ON public.licenses FOR ALL
  USING (
    EXISTS (
      SELECT 1 FROM public.profiles
      WHERE id = auth.uid() AND (is_admin = true OR 'venue_admin' = ANY(admin_roles))
    )
  );

-- Auto-generate license_id based on type prefix
CREATE OR REPLACE FUNCTION generate_license_id()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE
  prefix TEXT;
  candidate TEXT;
BEGIN
  IF NEW.license_id IS NULL OR NEW.license_id = '' THEN
    prefix := CASE NEW.license_type
      WHEN 'venue_owner'  THEN 'ESP-VO-'
      WHEN 'organizer'    THEN 'ESP-OR-'
      WHEN 'broadcaster'  THEN 'ESP-BC-'
      ELSE 'ESP-XX-'
    END;
    LOOP
      candidate := prefix || LPAD((FLOOR(RANDOM() * 900000) + 100000)::TEXT, 6, '0');
      EXIT WHEN NOT EXISTS (SELECT 1 FROM public.licenses WHERE license_id = candidate);
    END LOOP;
    NEW.license_id := candidate;
  END IF;
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS set_license_id ON public.licenses;
CREATE TRIGGER set_license_id
  BEFORE INSERT ON public.licenses
  FOR EACH ROW EXECUTE FUNCTION generate_license_id();

-- ── 20260303120000_venue_impressions_and_geo.sql ────────────────────────────────────────────────────────
-- Venue impressions tracking (fire-and-forget analytics)
CREATE TABLE IF NOT EXISTS public.venue_impressions (
  id UUID DEFAULT gen_random_uuid() PRIMARY KEY,
  venue_id UUID NOT NULL REFERENCES public.venues(id) ON DELETE CASCADE,
  event_type TEXT NOT NULL
    CONSTRAINT impressions_type_check CHECK (
      event_type IN ('view','card_view','booking_click','contact_click')
    ),
  user_id UUID REFERENCES public.profiles(id) ON DELETE SET NULL,
  session_id TEXT,
  created_at TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE public.venue_impressions ENABLE ROW LEVEL SECURITY;

-- Anyone can insert impressions (fire-and-forget tracking)
CREATE POLICY "Anyone can log impressions"
  ON public.venue_impressions FOR INSERT
  WITH CHECK (true);

-- Venue owners can read their own venue impressions
-- Use alias + explicit table prefix to avoid ambiguity with venues.venue_id (TEXT)
CREATE POLICY "Owners see own venue impressions"
  ON public.venue_impressions FOR SELECT
  USING (
    EXISTS (
      SELECT 1 FROM public.venues v
      WHERE v.id = venue_impressions.venue_id AND v.owner_id = auth.uid()
    )
  );

-- Index for fast per-venue queries
CREATE INDEX IF NOT EXISTS idx_venue_impressions_venue_id
  ON public.venue_impressions (venue_id, event_type, created_at DESC);

-- Geo search using Haversine formula — no PostGIS required
CREATE OR REPLACE FUNCTION find_nearby_venues(
  user_lat FLOAT,
  user_lng  FLOAT,
  radius_km FLOAT DEFAULT 50
)
RETURNS SETOF public.venues
LANGUAGE sql STABLE AS $$
  SELECT v.*
  FROM public.venues v
  WHERE
    v.status = 'published'
    AND v.latitude  IS NOT NULL
    AND v.longitude IS NOT NULL
    AND (
      6371 * acos(
        LEAST(1.0,
          cos(radians(user_lat)) * cos(radians(v.latitude)) *
          cos(radians(v.longitude) - radians(user_lng)) +
          sin(radians(user_lat)) * sin(radians(v.latitude))
        )
      )
    ) <= radius_km
  ORDER BY (
      6371 * acos(
        LEAST(1.0,
          cos(radians(user_lat)) * cos(radians(v.latitude)) *
          cos(radians(v.longitude) - radians(user_lng)) +
          sin(radians(user_lat)) * sin(radians(v.latitude))
        )
      )
    ) ASC;
$$;

-- ── 20260303130000_venue_live_status.sql ────────────────────────────────────────────────────────
-- Real-time live seating status pushed by Esportra Desktop Agent
CREATE TABLE IF NOT EXISTS public.venue_live_status (
  venue_id UUID PRIMARY KEY REFERENCES public.venues(id) ON DELETE CASCADE,
  seats_total INTEGER NOT NULL DEFAULT 0,
  seats_occupied INTEGER NOT NULL DEFAULT 0,
  is_open BOOLEAN NOT NULL DEFAULT false,
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE public.venue_live_status ENABLE ROW LEVEL SECURITY;

-- Public can read live status (shown on VenueDetails)
CREATE POLICY "Anyone can read live status"
  ON public.venue_live_status FOR SELECT
  USING (true);

-- Venue owners can upsert their own venue's live status
CREATE POLICY "Owners update own live status"
  ON public.venue_live_status FOR ALL
  USING (
    EXISTS (
      SELECT 1 FROM public.venues v
      WHERE v.id = venue_live_status.venue_id AND v.owner_id = auth.uid()
    )
  );

-- Enable Supabase Realtime on this table
ALTER TABLE public.venue_live_status REPLICA IDENTITY FULL;

-- ── 20260303140000_backfill_licenses.sql ────────────────────────────────────────────────────────
-- Backfill license records for existing users
-- venue_owner: any user who owns at least one venue
-- organizer: any user with role = 'organizer'
-- After inserting, link profiles.license_id to their primary license

-- 1. Venue owner licenses (source of truth: venues.owner_id)
INSERT INTO public.licenses (user_id, license_type)
SELECT DISTINCT v.owner_id, 'venue_owner'
FROM public.venues v
WHERE v.owner_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 FROM public.licenses l
    WHERE l.user_id = v.owner_id AND l.license_type = 'venue_owner'
  );

-- 2. Organizer licenses (source of truth: profiles.role = 'organizer')
INSERT INTO public.licenses (user_id, license_type)
SELECT p.id, 'organizer'
FROM public.profiles p
WHERE p.role = 'organizer'
  AND NOT EXISTS (
    SELECT 1 FROM public.licenses l
    WHERE l.user_id = p.id AND l.license_type = 'organizer'
  );

-- 3. Link profiles.license_id → their earliest active license
--    Only updates rows where license_id is currently NULL
UPDATE public.profiles p
SET license_id = (
  SELECT l.id
  FROM public.licenses l
  WHERE l.user_id = p.id
    AND l.status = 'active'
  ORDER BY l.issued_at ASC
  LIMIT 1
)
WHERE p.license_id IS NULL
  AND EXISTS (
    SELECT 1 FROM public.licenses l WHERE l.user_id = p.id
  );

-- ── 20260304000000_enable_veto_realtime.sql ────────────────────────────────────────────────────────
-- Enable Supabase Realtime for map veto tables.
-- Both tables need:
--   1. REPLICA IDENTITY FULL so that row-level filters (match_id=eq.X)
--      work correctly on UPDATE and DELETE events.
--   2. Added to the supabase_realtime publication so events actually fire.

ALTER TABLE public.match_map_vetos REPLICA IDENTITY FULL;
ALTER TABLE public.match_map_veto_actions REPLICA IDENTITY FULL;

-- Add to publication (idempotent — IF EXISTS guard not needed, ADD is safe to re-run)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_publication_tables
        WHERE pubname = 'supabase_realtime'
          AND schemaname = 'public'
          AND tablename = 'match_map_vetos'
    ) THEN
        ALTER PUBLICATION supabase_realtime ADD TABLE public.match_map_vetos;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_publication_tables
        WHERE pubname = 'supabase_realtime'
          AND schemaname = 'public'
          AND tablename = 'match_map_veto_actions'
    ) THEN
        ALTER PUBLICATION supabase_realtime ADD TABLE public.match_map_veto_actions;
    END IF;
END $$;

-- ── 20260304000001_finalize_match_locked_optional_scores.sql ────────────────────────────────────────────────────────
-- Make p_team1_score and p_team2_score optional (DEFAULT NULL) in finalize_match_locked.
--
-- The process-match-result edge function calls this with 4 params (no scores) because
-- it tracks per-map scores in brkt_match_games separately.
-- The bracket UI callers (GraphBracket, ManualAdjustmentMenu) call with 6 params.
-- Making scores optional with COALESCE preserves existing score values when not supplied.

CREATE OR REPLACE FUNCTION public.finalize_match_locked(
    p_match_id          UUID,
    p_expected_version  INTEGER,
    p_winner_id         UUID,
    p_loser_id          UUID,
    p_team1_score       INTEGER DEFAULT NULL,
    p_team2_score       INTEGER DEFAULT NULL
)
RETURNS BOOLEAN
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
    v_actual_version  INTEGER;
    v_tournament_id   UUID;
    v_is_authorized   BOOLEAN;
BEGIN
    -- 1. Pessimistic Lock
    SELECT version, v.tournament_id
    INTO v_actual_version, v_tournament_id
    FROM public.brkt_matches m
    JOIN public.brkt_versions v ON m.version_id = v.id
    WHERE m.id = p_match_id
    FOR UPDATE;

    IF v_actual_version IS NULL THEN
        RETURN FALSE;
    END IF;

    -- 2. Version Check
    IF v_actual_version != p_expected_version THEN
        RAISE EXCEPTION 'Match version mismatch. Expected %, got %', p_expected_version, v_actual_version;
    END IF;

    -- 3. Authorization Check
    SELECT EXISTS (
        SELECT 1 FROM public.profiles p
        WHERE p.id = auth.uid()
          AND (p.is_admin = TRUE OR p.role = 'admin')
    ) OR EXISTS (
        SELECT 1 FROM public.tournaments t
        WHERE t.id = v_tournament_id
          AND (
            t.organizer_id = auth.uid()
            OR t.organization_id IN (SELECT id FROM organizations WHERE owner_id = auth.uid())
          )
    ) INTO v_is_authorized;

    -- Edge Functions run as service role (auth.uid() is NULL); allow them through.
    IF auth.uid() IS NOT NULL AND NOT v_is_authorized THEN
        RAISE EXCEPTION 'Unauthorized to finalize this match';
    END IF;

    -- 4. Atomic Score Update
    -- When scores are not supplied (edge function path), keep existing values via COALESCE.
    UPDATE public.brkt_matches
    SET
        winner_id   = p_winner_id,
        loser_id    = p_loser_id,
        team1_score = COALESCE(p_team1_score, team1_score),
        team2_score = COALESCE(p_team2_score, team2_score),
        status      = 'completed',
        version     = version + 1,
        updated_at  = now()
    WHERE id = p_match_id;

    -- 5. Instant Database-Side Progression
    PERFORM public.proc_internal_advance_match(p_match_id, p_winner_id, p_loser_id);

    -- 6. External Notify Event
    INSERT INTO public.match_completed_events (match_id, winner_id, loser_id, status)
    VALUES (p_match_id, p_winner_id, p_loser_id, 'processed');

    RETURN TRUE;
END;
$$;

-- ── 20260304000002_enable_captain_page_realtime.sql ────────────────────────────────────────────────────────
-- Enable Supabase Realtime for tables subscribed to in CaptainMatchPage
-- and useMatchResultReport hook.
--
-- Without REPLICA IDENTITY FULL + publication membership, all postgres_changes
-- subscriptions on these tables are silent (no events delivered).

ALTER TABLE public.brkt_matches REPLICA IDENTITY FULL;
ALTER TABLE public.brkt_match_games REPLICA IDENTITY FULL;
ALTER TABLE public.match_result_reports REPLICA IDENTITY FULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_publication_tables
        WHERE pubname = 'supabase_realtime'
          AND schemaname = 'public'
          AND tablename = 'brkt_matches'
    ) THEN
        ALTER PUBLICATION supabase_realtime ADD TABLE public.brkt_matches;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_publication_tables
        WHERE pubname = 'supabase_realtime'
          AND schemaname = 'public'
          AND tablename = 'brkt_match_games'
    ) THEN
        ALTER PUBLICATION supabase_realtime ADD TABLE public.brkt_match_games;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_publication_tables
        WHERE pubname = 'supabase_realtime'
          AND schemaname = 'public'
          AND tablename = 'match_result_reports'
    ) THEN
        ALTER PUBLICATION supabase_realtime ADD TABLE public.match_result_reports;
    END IF;
END $$;

-- ── 20260304000003_match_disputes_and_dispute_rls.sql ────────────────────────────────────────────────────────
-- 1. Create match_disputes table (referenced by useMatchDispute and useMatchResultReport
--    but never existed in the DB).

CREATE TABLE IF NOT EXISTS public.match_disputes (
    id                    UUID DEFAULT gen_random_uuid() PRIMARY KEY,
    match_id              UUID NOT NULL,
    disputed_by_team_id   UUID REFERENCES public.teams(id) ON DELETE SET NULL,
    disputed_by_user_id   UUID REFERENCES public.profiles(id) ON DELETE SET NULL,
    reason                TEXT NOT NULL,
    evidence_urls         TEXT[] DEFAULT '{}',
    status                TEXT NOT NULL DEFAULT 'pending'
        CONSTRAINT match_disputes_status_check CHECK (status IN ('pending', 'resolved', 'rejected')),
    resolution            TEXT,
    resolved_at           TIMESTAMPTZ,
    resolved_by           UUID REFERENCES public.profiles(id) ON DELETE SET NULL,
    created_at            TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE public.match_disputes ENABLE ROW LEVEL SECURITY;

-- Participants in the match can insert disputes
CREATE POLICY "Participants can file disputes" ON public.match_disputes
    FOR INSERT WITH CHECK (disputed_by_user_id = auth.uid());

-- Anyone involved can view disputes for their match
CREATE POLICY "Participants and organizers can view disputes" ON public.match_disputes
    FOR SELECT USING (true);

-- Only organizers/admins can update (resolve/reject)
CREATE POLICY "Organizers can resolve disputes" ON public.match_disputes
    FOR UPDATE USING (
        EXISTS (
            SELECT 1 FROM public.profiles
            WHERE id = auth.uid()
              AND (is_admin = true OR role = 'admin')
        )
        OR EXISTS (
            SELECT 1 FROM public.brkt_matches m
            JOIN public.brkt_versions v ON m.version_id = v.id
            JOIN public.tournaments t ON v.tournament_id = t.id
            WHERE m.id = match_disputes.match_id
              AND t.organizer_id = auth.uid()
        )
    );

-- 2. Fix match_result_reports UPDATE policy.
--    The current policy only allows service_role and organizers.
--    Opposing captains must be able to mark a report as 'disputed'.
--    We extend the policy to also allow participants (team captains/members)
--    who are part of the match but did NOT submit the original report.

DROP POLICY IF EXISTS match_result_reports_update ON public.match_result_reports;

CREATE POLICY match_result_reports_update ON public.match_result_reports
    FOR UPDATE USING (
        -- Service role (edge functions)
        (SELECT auth.role()) = 'service_role'
        OR
        -- Tournament organizer
        EXISTS (
            SELECT 1 FROM public.brkt_matches m
            JOIN public.brkt_versions v ON m.version_id = v.id
            JOIN public.tournaments t ON v.tournament_id = t.id
            WHERE m.id = match_result_reports.match_id
              AND t.organizer_id = auth.uid()
        )
        OR
        -- Opposing captain: authenticated, did not file the report,
        -- and is a participant in the tournament that owns this match
        (
            auth.uid() IS NOT NULL
            AND auth.uid() != match_result_reports.reported_by
            AND EXISTS (
                SELECT 1 FROM public.brkt_matches m
                JOIN public.brkt_versions v ON m.version_id = v.id
                JOIN public.tournament_participants tp ON tp.tournament_id = v.tournament_id
                WHERE m.id = match_result_reports.match_id
                  AND (
                    tp.user_id = auth.uid()
                    OR tp.team_id IN (
                        SELECT team_id FROM public.team_members
                        WHERE user_id = auth.uid() AND is_active = true
                    )
                  )
            )
        )
    );

-- ── 20260304000004_file_match_result_dispute_rpc.sql ────────────────────────────────────────────────────────
-- Atomic RPC: file a match result dispute.
-- Replaces the fragile 2-step UPDATE+INSERT in useMatchResultReport.disputeReport().
-- Writes to BOTH match_disputes AND tournament_disputes (so the organizer's
-- dispute tab in /organizer/tournament/:slug?tab=disputes shows it),
-- then fires notifications to the original reporter and the tournament organizer.

CREATE OR REPLACE FUNCTION public.file_match_result_dispute(
    p_report_id   UUID,
    p_match_id    UUID,
    p_team_id     UUID,
    p_reason      TEXT
)
RETURNS VOID
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
    v_tournament_id   UUID;
    v_organizer_id    UUID;
    v_reporter_id     UUID;
    v_tournament_slug TEXT;
BEGIN
    -- Resolve tournament + reporter from report → match → version → tournament
    SELECT v.tournament_id, t.organizer_id, r.reported_by, t.slug
    INTO   v_tournament_id, v_organizer_id, v_reporter_id, v_tournament_slug
    FROM   public.match_result_reports r
    JOIN   public.brkt_matches          m ON m.id = r.match_id
    JOIN   public.brkt_versions         v ON v.id = m.version_id
    JOIN   public.tournaments           t ON t.id = v.tournament_id
    WHERE  r.id = p_report_id;

    -- 1. Mark the result report as disputed
    UPDATE public.match_result_reports
    SET    status         = 'disputed',
           responded_by   = auth.uid(),
           responded_at   = NOW(),
           dispute_reason = p_reason
    WHERE  id = p_report_id;

    -- 2. Create match-level dispute entry (used by useMatchDispute hook)
    INSERT INTO public.match_disputes
        (match_id, disputed_by_team_id, disputed_by_user_id, reason, evidence_urls, status)
    VALUES
        (p_match_id, p_team_id, auth.uid(), p_reason, '{}', 'pending');

    -- 3. Create tournament-level dispute entry (visible in tournament's disputes tab)
    INSERT INTO public.tournament_disputes
        (tournament_id, match_id, raised_by_user_id, team_id,
         title, description, status, dispute_reason)
    VALUES
        (v_tournament_id, p_match_id, auth.uid(), p_team_id,
         'Match Result Disputed', p_reason, 'open', 'result_dispute');

    -- 4a. Notify the reporter that their result is being disputed
    IF v_reporter_id IS NOT NULL THEN
        INSERT INTO public.notifications
            (user_id, type, title, message, link, data, is_read)
        VALUES
            (v_reporter_id, 'result_disputed',
             'Match Result Disputed',
             'The opposing team has disputed your reported result. An organizer will review.',
             '/tournaments/captain',
             jsonb_build_object('match_id', p_match_id),
             false);
    END IF;

    -- 4b. Notify the tournament organizer (link directly to tournament disputes tab)
    IF v_organizer_id IS NOT NULL THEN
        INSERT INTO public.notifications
            (user_id, type, title, message, link, data, is_read)
        VALUES
            (v_organizer_id, 'dispute_filed',
             'Match Dispute Filed',
             'A team has disputed a match result in your tournament. Review in the Disputes tab.',
             '/organizer/tournament/' || COALESCE(v_tournament_slug, v_tournament_id::TEXT) || '?tab=disputes',
             jsonb_build_object('match_id', p_match_id, 'tournament_id', v_tournament_id),
             false);
    END IF;
END;
$$;

GRANT EXECUTE ON FUNCTION public.file_match_result_dispute(UUID, UUID, UUID, TEXT) TO authenticated;

-- ── 20260304000005_match_ready_notification_trigger.sql ────────────────────────────────────────────────────────
-- Notify both team captains when a bracket match becomes ready (both teams assigned).
-- Fires on INSERT (first-round matches seeded with both teams) AND
-- on UPDATE (subsequent rounds where a team slot is filled after bracket advancement).

CREATE OR REPLACE FUNCTION public.notify_match_ready()
RETURNS TRIGGER
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
    rec RECORD;
BEGIN
    -- Only proceed when both teams are now assigned
    IF NEW.team1_id IS NULL OR NEW.team2_id IS NULL THEN
        RETURN NEW;
    END IF;

    -- On UPDATE: only fire when a previously-null slot was just filled
    IF TG_OP = 'UPDATE' THEN
        IF OLD.team1_id IS NOT NULL AND OLD.team2_id IS NOT NULL THEN
            RETURN NEW;   -- already had both teams — skip
        END IF;
    END IF;

    -- Fetch both team captains and send notifications
    FOR rec IN
        SELECT tm.user_id
        FROM   public.team_members tm
        WHERE  tm.team_id IN (NEW.team1_id, NEW.team2_id)
          AND  tm.role = 'captain'
          AND  tm.is_active = true
    LOOP
        INSERT INTO public.notifications
            (user_id, type, title, message, link, data, is_read)
        VALUES
            (rec.user_id, 'match_ready',
             'Match Ready',
             'Your match is ready. Head to the Captain dashboard to start the map veto.',
             '/tournaments/captain',
             jsonb_build_object('match_id', NEW.id),
             false);
    END LOOP;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_match_ready_notify ON public.brkt_matches;

CREATE TRIGGER trg_match_ready_notify
    AFTER INSERT OR UPDATE OF team1_id, team2_id ON public.brkt_matches
    FOR EACH ROW
    EXECUTE FUNCTION public.notify_match_ready();

-- ── 20260304000006_notifications_link_and_new_types.sql ────────────────────────────────────────────────────────
-- Add link column to notifications (referenced by all new notification inserts
-- and the match_ready DB trigger, but never existed in the schema).

ALTER TABLE public.notifications
    ADD COLUMN IF NOT EXISTS link TEXT;

-- Extend notification_type enum with match-flow event types.
-- We add them individually to avoid conflicts if some already exist.

DO $$
BEGIN
    -- Match result flow
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'result_reported'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'result_reported';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'result_accepted'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'result_accepted';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'result_disputed'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'result_disputed';
    END IF;

    -- Dispute flow
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'dispute_filed'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'dispute_filed';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'dispute_resolved'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'dispute_resolved';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'dispute_rejected'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'dispute_rejected';
    END IF;

    -- Map veto flow
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'veto_your_turn'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'veto_your_turn';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'veto_completed'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'veto_completed';
    END IF;

    -- Match lifecycle
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'match_ready'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'match_ready';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'match_completed'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'match_completed';
    END IF;

    -- Tournament registration
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'tournament_registered'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'tournament_registered';
    END IF;
END;
$$;

-- ── 20260304000007_dispute_storage_buckets.sql ────────────────────────────────────────────────────────
-- Create storage buckets needed for dispute evidence and match evidence.
-- Uses ON CONFLICT to be idempotent (safe to run if buckets already exist).

INSERT INTO storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
VALUES
    ('match-evidence',
     'match-evidence',
     true,
     5242880,  -- 5 MB
     ARRAY['image/jpeg','image/png','image/webp','image/gif','image/jpg'])
ON CONFLICT (id) DO NOTHING;

INSERT INTO storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
VALUES
    ('tournaments.disputes.evidence',
     'tournaments.disputes.evidence',
     true,
     5242880,
     ARRAY['image/jpeg','image/png','image/webp','image/gif','image/jpg'])
ON CONFLICT (id) DO NOTHING;

-- ── match-evidence policies ────────────────────────────────────────────────

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'match_evidence_public_select'
    ) THEN
        CREATE POLICY match_evidence_public_select
            ON storage.objects FOR SELECT
            TO public
            USING (bucket_id = 'match-evidence');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'match_evidence_auth_insert'
    ) THEN
        CREATE POLICY match_evidence_auth_insert
            ON storage.objects FOR INSERT
            TO authenticated
            WITH CHECK (bucket_id = 'match-evidence');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'match_evidence_auth_delete'
    ) THEN
        CREATE POLICY match_evidence_auth_delete
            ON storage.objects FOR DELETE
            TO authenticated
            USING (bucket_id = 'match-evidence' AND owner = auth.uid());
    END IF;
END;
$$;

-- ── tournaments.disputes.evidence policies ─────────────────────────────────

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'dispute_evidence_public_select'
    ) THEN
        CREATE POLICY dispute_evidence_public_select
            ON storage.objects FOR SELECT
            TO public
            USING (bucket_id = 'tournaments.disputes.evidence');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'dispute_evidence_auth_insert'
    ) THEN
        CREATE POLICY dispute_evidence_auth_insert
            ON storage.objects FOR INSERT
            TO authenticated
            WITH CHECK (bucket_id = 'tournaments.disputes.evidence');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'storage'
          AND tablename  = 'objects'
          AND policyname = 'dispute_evidence_auth_delete'
    ) THEN
        CREATE POLICY dispute_evidence_auth_delete
            ON storage.objects FOR DELETE
            TO authenticated
            USING (bucket_id = 'tournaments.disputes.evidence' AND owner = auth.uid());
    END IF;
END;
$$;

-- ── 20260304000008_dispute_comments_rls_fix.sql ────────────────────────────────────────────────────────
-- Fix dispute_comments RLS policies so that:
-- INSERT: both captains in the match (not just the one who filed) + organizer + staff + admin
-- SELECT: both captains + organizer + staff + admin (non-internal only for captains)

-- ── INSERT policy ─────────────────────────────────────────────────────────

DROP POLICY IF EXISTS dc_insert_own_or_organizer ON public.dispute_comments;

CREATE POLICY dc_insert_own_or_organizer ON public.dispute_comments
FOR INSERT WITH CHECK (
    user_id = auth.uid()
    AND (
        -- Person who raised the dispute
        EXISTS (
            SELECT 1 FROM public.tournament_disputes td
            WHERE td.id = dispute_comments.dispute_id
              AND td.raised_by_user_id = auth.uid()
        )
        OR
        -- Any active member of either team in the disputed match
        -- (covers the other captain whose result was disputed)
        EXISTS (
            SELECT 1 FROM public.tournament_disputes td
            JOIN public.brkt_matches bm ON bm.id = td.match_id
            JOIN public.team_members tm ON tm.team_id IN (bm.team1_id, bm.team2_id)
            WHERE td.id = dispute_comments.dispute_id
              AND tm.user_id = auth.uid()
              AND tm.is_active = true
        )
        OR
        -- Tournament organizer or assigned reviewer
        EXISTS (
            SELECT 1 FROM public.tournament_disputes td
            JOIN public.tournaments t ON t.id = td.tournament_id
            WHERE td.id = dispute_comments.dispute_id
              AND (t.organizer_id = auth.uid() OR td.assigned_to_user_id = auth.uid())
        )
        OR
        -- Tournament staff with disputes:assist permission
        EXISTS (
            SELECT 1 FROM public.tournament_disputes td
            JOIN public.tournament_staff ts ON ts.tournament_id = td.tournament_id
            WHERE td.id = dispute_comments.dispute_id
              AND ts.user_id = auth.uid()
              AND ts.status = 'active'
              AND 'disputes:assist' = ANY(ts.permissions)
        )
        OR
        -- Admin/moderator via profiles.admin_roles
        EXISTS (
            SELECT 1 FROM public.profiles p
            WHERE p.id = auth.uid()
              AND ('moderator' = ANY(p.admin_roles) OR 'ops_admin' = ANY(p.admin_roles))
        )
        OR
        -- Admin/moderator via admin_user_roles table
        EXISTS (
            SELECT 1 FROM public.admin_user_roles aur
            JOIN public.admin_roles ar ON ar.id = aur.role_id
            WHERE aur.user_id = auth.uid()
              AND lower(ar.name) = ANY(ARRAY['moderator', 'ops_admin'])
        )
        OR
        (SELECT auth.role()) = 'service_role'
    )
);

-- ── SELECT policy ─────────────────────────────────────────────────────────
-- Allow both match participants to see non-internal comments;
-- organizers/staff/admins can see all (including is_internal = true).

DROP POLICY IF EXISTS dc_select_own_or_dispute ON public.dispute_comments;

CREATE POLICY dc_select_own_or_dispute ON public.dispute_comments
FOR SELECT USING (
    -- Own comment
    user_id = auth.uid()
    OR
    -- Non-internal comment visible to any match participant (both teams)
    (
        NOT is_internal
        AND EXISTS (
            SELECT 1 FROM public.tournament_disputes td
            JOIN public.brkt_matches bm ON bm.id = td.match_id
            JOIN public.team_members tm ON tm.team_id IN (bm.team1_id, bm.team2_id)
            WHERE td.id = dispute_comments.dispute_id
              AND tm.user_id = auth.uid()
              AND tm.is_active = true
        )
    )
    OR
    -- Tournament organizer or assigned reviewer (sees all, including internal)
    EXISTS (
        SELECT 1 FROM public.tournament_disputes td
        JOIN public.tournaments t ON t.id = td.tournament_id
        WHERE td.id = dispute_comments.dispute_id
          AND (t.organizer_id = auth.uid() OR td.assigned_to_user_id = auth.uid())
    )
    OR
    -- Tournament staff with disputes:assist
    EXISTS (
        SELECT 1 FROM public.tournament_disputes td
        JOIN public.tournament_staff ts ON ts.tournament_id = td.tournament_id
        WHERE td.id = dispute_comments.dispute_id
          AND ts.user_id = auth.uid()
          AND ts.status = 'active'
          AND 'disputes:assist' = ANY(ts.permissions)
    )
    OR
    -- Admin/moderator via profiles
    EXISTS (
        SELECT 1 FROM public.profiles p
        WHERE p.id = auth.uid()
          AND ('moderator' = ANY(p.admin_roles) OR 'ops_admin' = ANY(p.admin_roles))
    )
    OR
    -- Admin/moderator via admin_user_roles
    EXISTS (
        SELECT 1 FROM public.admin_user_roles aur
        JOIN public.admin_roles ar ON ar.id = aur.role_id
        WHERE aur.user_id = auth.uid()
          AND lower(ar.name) = ANY(ARRAY['moderator', 'ops_admin'])
    )
    OR
    (SELECT auth.role()) = 'service_role'
);

-- ── 20260304000009_notify_admins_rpc.sql ────────────────────────────────────────────────────────
-- SECURITY DEFINER RPC that notifies all moderators + ops_admins.
-- Called from the frontend after a dispute is filed that goes to admin
-- (cheating, unsportsmanlike, other, general_support).
-- Running as SECURITY DEFINER so the frontend doesn't need SELECT on admin tables.

CREATE OR REPLACE FUNCTION public.notify_admins_of_dispute(
  p_dispute_id  UUID,
  p_type        TEXT,
  p_title       TEXT,
  p_message     TEXT,
  p_link        TEXT
)
RETURNS VOID
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
  v_admin_ids UUID[];
BEGIN
  -- Collect moderators / ops_admins from profiles.admin_roles
  SELECT ARRAY_AGG(DISTINCT p.id)
  INTO v_admin_ids
  FROM public.profiles p
  WHERE 'moderator' = ANY(p.admin_roles)
     OR 'ops_admin'  = ANY(p.admin_roles);

  -- Also include users in admin_user_roles with those role names
  SELECT ARRAY_AGG(DISTINCT aur.user_id) || COALESCE(v_admin_ids, '{}')
  INTO v_admin_ids
  FROM public.admin_user_roles aur
  JOIN public.admin_roles ar ON ar.id = aur.role_id
  WHERE lower(ar.name) = ANY(ARRAY['moderator', 'ops_admin']);

  -- Deduplicate and insert one notification per admin
  INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
  SELECT DISTINCT uid, p_type, p_title, p_message, p_link,
         jsonb_build_object('dispute_id', p_dispute_id),
         false
  FROM UNNEST(v_admin_ids) AS uid
  WHERE uid IS NOT NULL;
END;
$$;

GRANT EXECUTE ON FUNCTION public.notify_admins_of_dispute TO authenticated;

-- ── 20260304000010_seed_admin_roles.sql ────────────────────────────────────────────────────────
-- Ensure admin_roles table exists with all required columns.
CREATE TABLE IF NOT EXISTS public.admin_roles (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name        TEXT NOT NULL UNIQUE,
  key         TEXT UNIQUE,
  description TEXT,
  created_at  TIMESTAMPTZ DEFAULT now()
);

-- Add key column if it was missing from an older schema.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name   = 'admin_roles'
      AND column_name  = 'key'
  ) THEN
    ALTER TABLE public.admin_roles ADD COLUMN key TEXT UNIQUE;
  END IF;
END;
$$;

-- Seed the four assignable admin roles (super_admin is never assigned via UI).
-- ON CONFLICT updates key + description so re-running is safe.
INSERT INTO public.admin_roles (name, key, description) VALUES
  ('ops_admin',     'ops_admin',     'Operations Admin — manages tournaments, venues, and platform users'),
  ('moderator',     'moderator',     'Moderator — handles disputes and content moderation'),
  ('finance_admin', 'finance_admin', 'Finance Admin — manages financial reports and billing'),
  ('support_admin', 'support_admin', 'Support Admin — handles user support and verification requests')
ON CONFLICT (name) DO UPDATE SET
  key         = EXCLUDED.key,
  description = EXCLUDED.description;

-- ── admin_user_roles ────────────────────────────────────────────────────────
-- Ensure the join table exists.
CREATE TABLE IF NOT EXISTS public.admin_user_roles (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id     UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
  role_id     UUID NOT NULL REFERENCES public.admin_roles(id) ON DELETE CASCADE,
  assigned_by UUID REFERENCES auth.users(id),
  assigned_at TIMESTAMPTZ DEFAULT now(),
  UNIQUE (user_id, role_id)
);

-- ── RLS ─────────────────────────────────────────────────────────────────────

-- admin_roles: role definitions are not sensitive — any authenticated user may
-- read them so the AdminAccess dropdown can populate.
ALTER TABLE public.admin_roles ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE schemaname = 'public'
      AND tablename  = 'admin_roles'
      AND policyname = 'admin_roles_select_authenticated'
  ) THEN
    CREATE POLICY admin_roles_select_authenticated ON public.admin_roles
      FOR SELECT TO authenticated USING (true);
  END IF;

  -- Only admins can mutate admin_roles rows.
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE schemaname = 'public'
      AND tablename  = 'admin_roles'
      AND policyname = 'admin_roles_write_admin_only'
  ) THEN
    CREATE POLICY admin_roles_write_admin_only ON public.admin_roles
      FOR ALL TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM public.profiles
          WHERE id = auth.uid() AND is_admin = true
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM public.profiles
          WHERE id = auth.uid() AND is_admin = true
        )
      );
  END IF;
END;
$$;

-- admin_user_roles: only admins can read or write assignments.
ALTER TABLE public.admin_user_roles ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE schemaname = 'public'
      AND tablename  = 'admin_user_roles'
      AND policyname = 'admin_user_roles_admin_all'
  ) THEN
    CREATE POLICY admin_user_roles_admin_all ON public.admin_user_roles
      FOR ALL TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM public.profiles
          WHERE id = auth.uid() AND is_admin = true
        )
      )
      WITH CHECK (
        EXISTS (
          SELECT 1 FROM public.profiles
          WHERE id = auth.uid() AND is_admin = true
        )
      );
  END IF;
END;
$$;

GRANT SELECT ON public.admin_roles TO authenticated;
GRANT ALL    ON public.admin_user_roles TO authenticated;

-- ── 20260313000000_games_metadata_cache.sql ────────────────────────────────────────────────────────
-- games_metadata: 7-day DB cache for RAWG API responses
-- Used by /api/games/search backend endpoint

CREATE TABLE IF NOT EXISTS public.games_metadata (
    game_name         TEXT        PRIMARY KEY,
    rawg_id           INTEGER,
    background_image  TEXT,
    last_updated      TIMESTAMPTZ NOT NULL DEFAULT now(),
    rawg_data         JSONB
);

-- Public read access (game images shown to all users)
ALTER TABLE public.games_metadata ENABLE ROW LEVEL SECURITY;

CREATE POLICY "games_metadata_public_read"
    ON public.games_metadata FOR SELECT
    USING (true);

-- ── 20260313000001_veto_game_column_and_notification_enum.sql ────────────────────────────────────────────────────────
-- Add game column to match_map_vetos (backend stores game per veto session)
ALTER TABLE public.match_map_vetos ADD COLUMN IF NOT EXISTS game TEXT DEFAULT 'valorant';

-- Add team_invite_response to notification_type enum (used when accepting/rejecting team invites)
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'team_invite_response';

-- ── 20260317_001_baseline.sql ────────────────────────────────────────────────────────
-- Baseline migration: marks the starting point for tracked migrations.
-- The existing schema was created before DbUp was introduced.
-- All tables already exist in staging and production as of 2026-03-17.
-- This file intentionally performs no DDL changes.

SELECT 1;

SELECT 'post-baseline replay schema applied (23 migrations)' AS status;
