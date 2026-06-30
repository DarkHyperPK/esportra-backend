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

-- ── Organization staff (20260325140000, 20260325150000) ────────────────────
CREATE TABLE IF NOT EXISTS public.organization_staff (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  organization_id UUID,
  user_id UUID,
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

ALTER TABLE public.verified_roles
  ADD COLUMN IF NOT EXISTS role TEXT;

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
