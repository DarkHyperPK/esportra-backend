-- ============================================================
-- DatHost game server integration: game_servers table + server_region on tournaments
-- ============================================================

-- 1. Add server_region to tournaments (DatHost location ID e.g. 'amsterdam', 'dubai')
ALTER TABLE public.tournaments
  ADD COLUMN IF NOT EXISTS server_region TEXT;

-- 2. Create game_servers table for tracking provisioned servers
CREATE TABLE IF NOT EXISTS public.game_servers (
  id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  match_id        UUID NOT NULL,
  tournament_id   UUID,
  provider        TEXT NOT NULL DEFAULT 'dathost',
  external_id     TEXT NOT NULL,
  region          TEXT NOT NULL,
  ip              TEXT,
  raw_ip          TEXT,
  port            INT,
  gotv_port       INT,
  rcon_password   TEXT,
  map             TEXT,
  status          TEXT NOT NULL DEFAULT 'provisioning',
  cost_per_hour   NUMERIC(8,4),
  server_name     TEXT,
  error_message   TEXT,
  started_at      TIMESTAMPTZ,
  stopped_at      TIMESTAMPTZ,
  deleted_at      TIMESTAMPTZ,
  created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT fk_game_servers_match FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE
);

-- Indexes
CREATE INDEX IF NOT EXISTS idx_game_servers_match_id ON public.game_servers(match_id);
CREATE INDEX IF NOT EXISTS idx_game_servers_status ON public.game_servers(status);
CREATE INDEX IF NOT EXISTS idx_game_servers_provider ON public.game_servers(provider);

-- 3. RLS
ALTER TABLE public.game_servers ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'game_servers' AND policyname = 'game_servers_select_authenticated') THEN
    CREATE POLICY game_servers_select_authenticated ON public.game_servers
      FOR SELECT TO authenticated
      USING (true);
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'game_servers' AND policyname = 'game_servers_insert_service') THEN
    CREATE POLICY game_servers_insert_service ON public.game_servers
      FOR INSERT TO service_role
      WITH CHECK (true);
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'game_servers' AND policyname = 'game_servers_update_service') THEN
    CREATE POLICY game_servers_update_service ON public.game_servers
      FOR UPDATE TO service_role
      USING (true);
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'game_servers' AND policyname = 'game_servers_delete_service') THEN
    CREATE POLICY game_servers_delete_service ON public.game_servers
      FOR DELETE TO service_role
      USING (true);
  END IF;
END $$;

-- 4. GRANTs
GRANT ALL ON public.game_servers TO service_role;
GRANT SELECT ON public.game_servers TO authenticated;
