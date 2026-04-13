-- ============================================================
-- Fix game_servers: RLS security, unique index, FK, updated_at trigger
-- ============================================================

-- 1. Drop the overly-permissive authenticated SELECT policy (exposes rcon_password)
--    All data access goes through the .NET API which excludes sensitive columns.
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'game_servers' AND policyname = 'game_servers_select_authenticated') THEN
    DROP POLICY game_servers_select_authenticated ON public.game_servers;
  END IF;
END $$;

-- Revoke direct SELECT from authenticated — API serves filtered data via service_role
REVOKE SELECT ON public.game_servers FROM authenticated;

-- Ensure service_role has a SELECT policy
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'game_servers' AND policyname = 'game_servers_select_service') THEN
    CREATE POLICY game_servers_select_service ON public.game_servers
      FOR SELECT TO service_role
      USING (true);
  END IF;
END $$;

-- 2. Partial unique index: prevent double-provisioning for the same match
CREATE UNIQUE INDEX IF NOT EXISTS idx_game_servers_match_active
  ON public.game_servers(match_id) WHERE deleted_at IS NULL;

-- 3. Add FK on tournament_id (was missing)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.table_constraints
    WHERE constraint_name = 'fk_game_servers_tournament' AND table_name = 'game_servers'
  ) THEN
    ALTER TABLE public.game_servers
      ADD CONSTRAINT fk_game_servers_tournament
      FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE SET NULL;
  END IF;
END $$;

-- 4. Auto-update updated_at on row changes
CREATE OR REPLACE FUNCTION update_game_servers_updated_at()
RETURNS TRIGGER AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_game_servers_updated_at') THEN
    CREATE TRIGGER trg_game_servers_updated_at
      BEFORE UPDATE ON public.game_servers
      FOR EACH ROW EXECUTE FUNCTION update_game_servers_updated_at();
  END IF;
END $$;
