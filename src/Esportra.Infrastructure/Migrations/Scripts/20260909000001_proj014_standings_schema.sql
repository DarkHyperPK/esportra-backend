-- ============================================================================
-- Migration: PROJ-014 Standings Infrastructure
-- Purpose:   Three supporting schema changes for the standings feature:
--            1. Composite index on brkt_matches(version_id, status) — speeds up
--               the standings query that filters by version and completion state.
--            2. status column on tournament_placements — tracks whether the
--               placement was manually confirmed or auto-resolved. TEXT, no CHECK
--               constraint (validated at application level). Valid values:
--               'confirmed', 'auto'. NULL = legacy / unset.
--            3. RLS enabled on leaderboard_team_stats — was created without RLS.
--               Public read, service_role write.
--            4. RLS enabled on leaderboard_refresh_state — was created without
--               RLS. Service_role only (internal watermark table, no public read).
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Composite index on brkt_matches(version_id, status)
-- ---------------------------------------------------------------------------
CREATE INDEX IF NOT EXISTS idx_brkt_matches_version_status
    ON public.brkt_matches (version_id, status);

-- ---------------------------------------------------------------------------
-- 2. status column on tournament_placements
-- ---------------------------------------------------------------------------
ALTER TABLE public.tournament_placements
    ADD COLUMN IF NOT EXISTS status TEXT DEFAULT NULL;

-- ---------------------------------------------------------------------------
-- 3. RLS on leaderboard_team_stats
-- ---------------------------------------------------------------------------
ALTER TABLE public.leaderboard_team_stats ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'leaderboard_team_stats'
          AND policyname = 'leaderboard_team_stats_public_select'
    ) THEN
        CREATE POLICY leaderboard_team_stats_public_select
            ON public.leaderboard_team_stats
            FOR SELECT
            USING (true);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'leaderboard_team_stats'
          AND policyname = 'leaderboard_team_stats_service_role_all'
    ) THEN
        CREATE POLICY leaderboard_team_stats_service_role_all
            ON public.leaderboard_team_stats
            FOR ALL
            TO service_role
            USING (true)
            WITH CHECK (true);
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- 4. RLS on leaderboard_refresh_state
-- ---------------------------------------------------------------------------
ALTER TABLE public.leaderboard_refresh_state ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'leaderboard_refresh_state'
          AND policyname = 'leaderboard_refresh_state_service_role_all'
    ) THEN
        CREATE POLICY leaderboard_refresh_state_service_role_all
            ON public.leaderboard_refresh_state
            FOR ALL
            TO service_role
            USING (true)
            WITH CHECK (true);
    END IF;
END $$;
