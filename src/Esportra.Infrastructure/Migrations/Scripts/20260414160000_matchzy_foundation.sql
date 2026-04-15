-- ============================================================================
-- Migration: 20260414160000_matchzy_foundation.sql
-- Purpose:   Foundation tables for MatchZy CS2 server integration.
--
--   1. player_steam_accounts  — links Esportra users to their Steam identity
--   2. match_player_stats     — per-player, per-map stats pushed by MatchZy
--   3. game_servers (ALTER)   — adds matchzy_secret, auto_managed, current_map_number
--
-- Runs as:   supabase_admin (via DbUp migration runner)
-- Idempotent: Yes — safe to re-run.
-- Rollback:   DROP TABLE player_steam_accounts, match_player_stats;
--             ALTER TABLE game_servers DROP COLUMN matchzy_secret,
--               DROP COLUMN auto_managed, DROP COLUMN current_map_number;
-- ============================================================================


-- ════════════════════════════════════════════════════════════════════════════
-- 1. player_steam_accounts
--    One Steam account per Esportra user.  Used to map MatchZy SteamID64
--    callbacks back to platform user_ids.
-- ════════════════════════════════════════════════════════════════════════════

CREATE TABLE IF NOT EXISTS public.player_steam_accounts (
  id          UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id     UUID        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
  steam64_id  TEXT        NOT NULL,
  steam_name  TEXT,
  avatar_url  TEXT,
  profile_url TEXT,
  linked_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
  verified    BOOLEAN     NOT NULL DEFAULT false,

  -- One Steam link per user, one user per Steam account
  CONSTRAINT uq_player_steam_accounts_user_id    UNIQUE (user_id),
  CONSTRAINT uq_player_steam_accounts_steam64_id UNIQUE (steam64_id)
);

-- Index on steam64_id for fast lookups when MatchZy posts stats by SteamID
CREATE INDEX IF NOT EXISTS idx_player_steam_accounts_steam64_id
  ON public.player_steam_accounts(steam64_id);

-- RLS -----------------------------------------------------------------------
ALTER TABLE public.player_steam_accounts ENABLE ROW LEVEL SECURITY;

-- Users can read their own Steam link
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'player_steam_accounts'
      AND policyname = 'steam_accounts_select_own'
  ) THEN
    CREATE POLICY steam_accounts_select_own ON public.player_steam_accounts
      FOR SELECT TO authenticated
      USING (auth.uid() = user_id);
  END IF;
END $$;

-- service_role: full CRUD (INSERT/UPDATE/DELETE) via API layer
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'player_steam_accounts'
      AND policyname = 'steam_accounts_select_service'
  ) THEN
    CREATE POLICY steam_accounts_select_service ON public.player_steam_accounts
      FOR SELECT TO service_role
      USING (true);
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'player_steam_accounts'
      AND policyname = 'steam_accounts_insert_service'
  ) THEN
    CREATE POLICY steam_accounts_insert_service ON public.player_steam_accounts
      FOR INSERT TO service_role
      WITH CHECK (true);
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'player_steam_accounts'
      AND policyname = 'steam_accounts_update_service'
  ) THEN
    CREATE POLICY steam_accounts_update_service ON public.player_steam_accounts
      FOR UPDATE TO service_role
      USING (true);
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'player_steam_accounts'
      AND policyname = 'steam_accounts_delete_service'
  ) THEN
    CREATE POLICY steam_accounts_delete_service ON public.player_steam_accounts
      FOR DELETE TO service_role
      USING (true);
  END IF;
END $$;

-- GRANTs --------------------------------------------------------------------
GRANT ALL ON public.player_steam_accounts TO service_role;
GRANT SELECT ON public.player_steam_accounts TO authenticated;


-- ════════════════════════════════════════════════════════════════════════════
-- 2. match_player_stats
--    Per-player, per-map scoreboard data pushed by the MatchZy server plugin
--    via the /matchzy/map-end webhook.  Stats are public — anyone can view.
-- ════════════════════════════════════════════════════════════════════════════

CREATE TABLE IF NOT EXISTS public.match_player_stats (
  id                 UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
  match_id           UUID        NOT NULL,
  map_number         INT         NOT NULL,            -- 0-indexed per MatchZy convention
  user_id            UUID,                             -- NULL if Steam account not linked
  steam64_id         TEXT        NOT NULL,
  team               TEXT        NOT NULL,
  player_name        TEXT,

  -- Core scoreboard
  kills              INT         NOT NULL DEFAULT 0,
  deaths             INT         NOT NULL DEFAULT 0,
  assists            INT         NOT NULL DEFAULT 0,
  flash_assists      INT         NOT NULL DEFAULT 0,
  team_kills         INT         NOT NULL DEFAULT 0,
  suicides           INT         NOT NULL DEFAULT 0,
  damage             INT         NOT NULL DEFAULT 0,
  utility_damage     INT         NOT NULL DEFAULT 0,
  enemies_flashed    INT         NOT NULL DEFAULT 0,
  friendlies_flashed INT         NOT NULL DEFAULT 0,
  knife_kills        INT         NOT NULL DEFAULT 0,
  headshot_kills     INT         NOT NULL DEFAULT 0,
  rounds_played      INT         NOT NULL DEFAULT 0,

  -- Objectives
  bomb_defuses       INT         NOT NULL DEFAULT 0,
  bomb_plants        INT         NOT NULL DEFAULT 0,

  -- Multi-kills per round
  "1k"               INT         NOT NULL DEFAULT 0,
  "2k"               INT         NOT NULL DEFAULT 0,
  "3k"               INT         NOT NULL DEFAULT 0,
  "4k"               INT         NOT NULL DEFAULT 0,
  "5k"               INT         NOT NULL DEFAULT 0,

  -- Clutch rounds won
  "1v1"              INT         NOT NULL DEFAULT 0,
  "1v2"              INT         NOT NULL DEFAULT 0,
  "1v3"              INT         NOT NULL DEFAULT 0,
  "1v4"              INT         NOT NULL DEFAULT 0,
  "1v5"              INT         NOT NULL DEFAULT 0,

  -- Opening duels
  first_kills_t      INT         NOT NULL DEFAULT 0,
  first_kills_ct     INT         NOT NULL DEFAULT 0,
  first_deaths_t     INT         NOT NULL DEFAULT 0,
  first_deaths_ct    INT         NOT NULL DEFAULT 0,

  -- Advanced
  trade_kills        INT         NOT NULL DEFAULT 0,
  kast               INT         NOT NULL DEFAULT 0,   -- percentage (0-100)
  score              INT         NOT NULL DEFAULT 0,
  mvp                INT         NOT NULL DEFAULT 0,

  created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),

  -- Foreign keys
  CONSTRAINT fk_match_player_stats_match
    FOREIGN KEY (match_id) REFERENCES public.brkt_matches(id) ON DELETE CASCADE,
  CONSTRAINT fk_match_player_stats_user
    FOREIGN KEY (user_id)  REFERENCES auth.users(id) ON DELETE SET NULL,

  -- Business rule: one stats row per player per map per match
  CONSTRAINT uq_match_player_stats_match_map_player
    UNIQUE (match_id, map_number, steam64_id),

  -- Validate team label matches MatchZy convention
  CONSTRAINT chk_match_player_stats_team
    CHECK (team IN ('team1', 'team2'))
);

-- Indexes for common query patterns
CREATE INDEX IF NOT EXISTS idx_match_player_stats_match_id
  ON public.match_player_stats(match_id);

CREATE INDEX IF NOT EXISTS idx_match_player_stats_user_id
  ON public.match_player_stats(user_id);

CREATE INDEX IF NOT EXISTS idx_match_player_stats_steam64_id
  ON public.match_player_stats(steam64_id);

-- RLS -----------------------------------------------------------------------
ALTER TABLE public.match_player_stats ENABLE ROW LEVEL SECURITY;

-- Stats are public data — anyone authenticated can view scoreboard
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'match_player_stats'
      AND policyname = 'match_stats_select_public'
  ) THEN
    CREATE POLICY match_stats_select_public ON public.match_player_stats
      FOR SELECT TO authenticated
      USING (true);
  END IF;
END $$;

-- service_role: full CRUD (webhook inserts, backfill updates)
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'match_player_stats'
      AND policyname = 'match_stats_select_service'
  ) THEN
    CREATE POLICY match_stats_select_service ON public.match_player_stats
      FOR SELECT TO service_role
      USING (true);
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'match_player_stats'
      AND policyname = 'match_stats_insert_service'
  ) THEN
    CREATE POLICY match_stats_insert_service ON public.match_player_stats
      FOR INSERT TO service_role
      WITH CHECK (true);
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'match_player_stats'
      AND policyname = 'match_stats_update_service'
  ) THEN
    CREATE POLICY match_stats_update_service ON public.match_player_stats
      FOR UPDATE TO service_role
      USING (true);
  END IF;
END $$;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'match_player_stats'
      AND policyname = 'match_stats_delete_service'
  ) THEN
    CREATE POLICY match_stats_delete_service ON public.match_player_stats
      FOR DELETE TO service_role
      USING (true);
  END IF;
END $$;

-- GRANTs --------------------------------------------------------------------
GRANT ALL ON public.match_player_stats TO service_role;
GRANT SELECT ON public.match_player_stats TO authenticated;


-- ════════════════════════════════════════════════════════════════════════════
-- 3. game_servers — add MatchZy integration columns
--    matchzy_secret:     shared HMAC secret for webhook authentication
--    auto_managed:       true if the server lifecycle is fully automated
--    current_map_number: tracks which map in a Bo3/Bo5 series is active
-- ════════════════════════════════════════════════════════════════════════════

ALTER TABLE public.game_servers
  ADD COLUMN IF NOT EXISTS matchzy_secret     TEXT;

ALTER TABLE public.game_servers
  ADD COLUMN IF NOT EXISTS auto_managed       BOOLEAN NOT NULL DEFAULT false;

ALTER TABLE public.game_servers
  ADD COLUMN IF NOT EXISTS current_map_number INT DEFAULT 0;


-- end of migration
