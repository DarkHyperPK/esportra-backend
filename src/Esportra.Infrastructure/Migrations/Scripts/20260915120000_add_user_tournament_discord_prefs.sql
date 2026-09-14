-- ============================================================================
-- Migration: User Tournament Discord DM Preferences
-- Purpose:   Creates user_tournament_discord_prefs table to store per-user,
--            per-tournament opt-in/out for Discord DM notifications.
--            One row per (user_id, tournament_id) pair; defaults to enabled.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Create user_tournament_discord_prefs table
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.user_tournament_discord_prefs (
    id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             UUID        NOT NULL REFERENCES public.profiles(id) ON DELETE CASCADE,
    tournament_id       UUID        NOT NULL REFERENCES public.tournaments(id) ON DELETE CASCADE,
    discord_dms_enabled BOOL        NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_utdp_user_tournament UNIQUE (user_id, tournament_id)
);

-- ---------------------------------------------------------------------------
-- 2. Indexes
-- ---------------------------------------------------------------------------

-- Primary access pattern: look up pref for a specific user
CREATE INDEX IF NOT EXISTS idx_utdp_user_id
    ON public.user_tournament_discord_prefs (user_id);

-- ---------------------------------------------------------------------------
-- 3. Row-Level Security
-- ---------------------------------------------------------------------------
ALTER TABLE public.user_tournament_discord_prefs ENABLE ROW LEVEL SECURITY;

-- Users may read only their own preferences
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'user_tournament_discord_prefs'
          AND policyname = 'utdp_user_read'
    ) THEN
        CREATE POLICY utdp_user_read
            ON public.user_tournament_discord_prefs
            FOR SELECT
            TO authenticated
            USING (user_id = auth.uid());
    END IF;
END $$;

-- Users may insert/update/delete only their own preferences
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'user_tournament_discord_prefs'
          AND policyname = 'utdp_user_write'
    ) THEN
        CREATE POLICY utdp_user_write
            ON public.user_tournament_discord_prefs
            FOR ALL
            TO authenticated
            USING (user_id = auth.uid())
            WITH CHECK (user_id = auth.uid());
    END IF;
END $$;

-- Service role has full access for background job reads and admin operations
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE schemaname = 'public'
          AND tablename  = 'user_tournament_discord_prefs'
          AND policyname = 'utdp_service_all'
    ) THEN
        CREATE POLICY utdp_service_all
            ON public.user_tournament_discord_prefs
            FOR ALL
            TO service_role
            USING (true)
            WITH CHECK (true);
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- 4. GRANTs
-- ---------------------------------------------------------------------------
GRANT SELECT, INSERT, UPDATE, DELETE ON public.user_tournament_discord_prefs TO authenticated;
GRANT ALL ON public.user_tournament_discord_prefs TO service_role;
