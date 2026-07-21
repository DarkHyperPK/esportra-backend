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

-- ── Audit logs columns (used by 20260409100000, 20260626000000) ────────────
ALTER TABLE public.audit_logs
  ADD COLUMN IF NOT EXISTS target_type TEXT,
  ADD COLUMN IF NOT EXISTS target_id UUID,
  ADD COLUMN IF NOT EXISTS action_type TEXT,
  ADD COLUMN IF NOT EXISTS admin_id UUID,
  ADD COLUMN IF NOT EXISTS details JSONB,
  ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ DEFAULT now();

ALTER TABLE public.staff_audit_log
  ADD COLUMN IF NOT EXISTS target_type TEXT,
  ADD COLUMN IF NOT EXISTS target_id UUID,
  ADD COLUMN IF NOT EXISTS action TEXT,
  ADD COLUMN IF NOT EXISTS details JSONB,
  ADD COLUMN IF NOT EXISTS actor_id UUID,
  ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ DEFAULT now();

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
  ADD COLUMN IF NOT EXISTS game TEXT,
  ADD COLUMN IF NOT EXISTS settings JSONB DEFAULT '{}'::jsonb;

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
  ADD COLUMN IF NOT EXISTS avatar_url TEXT,
  ADD COLUMN IF NOT EXISTS is_suspended BOOLEAN NOT NULL DEFAULT false;

ALTER TABLE public.audit_logs
  ADD COLUMN IF NOT EXISTS target_type TEXT,
  ADD COLUMN IF NOT EXISTS target_id UUID,
  ADD COLUMN IF NOT EXISTS action_type TEXT,
  ADD COLUMN IF NOT EXISTS admin_id UUID,
  ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ DEFAULT now();

-- ── Partner sponsor onboarding (legacy tables used by 20260714110000) ──────
ALTER TABLE public.sponsors
  ADD COLUMN IF NOT EXISTS name TEXT NOT NULL DEFAULT 'Legacy sponsor';

CREATE TABLE IF NOT EXISTS public.sponsor_accounts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
  sponsor_id UUID NOT NULL REFERENCES public.sponsors(id) ON DELETE CASCADE,
  role TEXT NOT NULL DEFAULT 'owner',
  onboarding_meta JSONB NOT NULL DEFAULT '{"completed":false,"current_step":0,"steps":{}}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS public.partner_applications (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  status TEXT NOT NULL DEFAULT 'pending',
  contact_email TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ── Match messages (20260609160000) ────────────────────────────────────────
CREATE TABLE IF NOT EXISTS public.match_messages (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  match_id UUID NOT NULL,
  sender_id UUID NOT NULL,
  sender_name TEXT,
  team_id UUID,
  content TEXT NOT NULL,
  message_type TEXT DEFAULT 'text',
  metadata JSONB,
  created_at TIMESTAMPTZ DEFAULT now()
);

-- ── BR rounds (legacy, dropped by 20260611120000_br_pro_lobby_model) ───────
CREATE TABLE IF NOT EXISTS public.br_rounds (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  group_id UUID NOT NULL,
  round_number INTEGER NOT NULL,
  lobby_code TEXT,
  status TEXT NOT NULL DEFAULT 'pending',
  scheduled_at TIMESTAMPTZ,
  started_at TIMESTAMPTZ,
  completed_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  queue_timer_minutes INTEGER,
  queue_started_at TIMESTAMPTZ
);

-- ── BR round results (legacy, renamed to br_lobby_results by 20260611120000) ─
CREATE TABLE IF NOT EXISTS public.br_round_results (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  round_id UUID NOT NULL,
  team_id UUID,
  placement INTEGER NOT NULL,
  kills INTEGER NOT NULL DEFAULT 0,
  placement_points INTEGER NOT NULL DEFAULT 0,
  kill_points INTEGER NOT NULL DEFAULT 0,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  participant_id UUID
);

-- ── BR round evidence (legacy, renamed to br_lobby_evidence by 20260611120000)
CREATE TABLE IF NOT EXISTS public.br_round_evidence (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  round_id UUID NOT NULL,
  team_id UUID,
  participant_id UUID,
  image_url TEXT NOT NULL,
  submitted_by UUID NOT NULL,
  submitted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  placement INTEGER,
  kills INTEGER,
  reviewed BOOLEAN NOT NULL DEFAULT false,
  reviewed_at TIMESTAMPTZ,
  reviewed_by UUID,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

SELECT 'replay legacy overlays applied' AS status;
