-- PROJ-076 Phase 3: Cached game stats for LFG card enrichment.
-- One row per (user_id, game_slug). Populated by ValorantStatsSyncService, never by users.
-- Generic table — game_slug scopes rows per game (Valorant today, extensible).

CREATE TABLE IF NOT EXISTS public.player_game_stats (
    id                    UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id               UUID        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    game_slug             TEXT        NOT NULL DEFAULT 'valorant',
    provider_player_id    TEXT        NOT NULL,
    competitive_tier      INT,
    competitive_tier_name TEXT,
    win_count             INT         NOT NULL DEFAULT 0,
    loss_count            INT         NOT NULL DEFAULT 0,
    total_kills           INT         NOT NULL DEFAULT 0,
    total_deaths          INT         NOT NULL DEFAULT 0,
    total_assists         INT         NOT NULL DEFAULT 0,
    matches_analyzed      INT         NOT NULL DEFAULT 0,
    popular_agents        JSONB       NOT NULL DEFAULT '[]'::jsonb,
    win_rate              NUMERIC(5,2),
    kd_ratio              NUMERIC(5,2),
    avg_score             INT,
    total_score           INT         NOT NULL DEFAULT 0,
    last_match_id         TEXT,
    last_act_id           TEXT,
    synced_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    sync_status           TEXT        NOT NULL DEFAULT 'pending',
    sync_error            TEXT,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ── Unique constraint ────────────────────────────────────────────────────────

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'pgs_user_game_unique') THEN
        ALTER TABLE public.player_game_stats
            ADD CONSTRAINT pgs_user_game_unique UNIQUE (user_id, game_slug);
    END IF;
END $$;

-- ── Indexes ──────────────────────────────────────────────────────────────────

CREATE INDEX IF NOT EXISTS idx_pgs_user ON public.player_game_stats (user_id);
CREATE INDEX IF NOT EXISTS idx_pgs_sync_status
    ON public.player_game_stats (sync_status)
    WHERE sync_status IN ('pending', 'error');
CREATE INDEX IF NOT EXISTS idx_pgs_synced_at
    ON public.player_game_stats (synced_at);

-- ── RLS ──────────────────────────────────────────────────────────────────────

ALTER TABLE public.player_game_stats ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS pgs_own ON public.player_game_stats;
CREATE POLICY pgs_own
    ON public.player_game_stats
    FOR SELECT
    TO authenticated
    USING (user_id = auth.uid());

DROP POLICY IF EXISTS pgs_public ON public.player_game_stats;
CREATE POLICY pgs_public
    ON public.player_game_stats
    FOR SELECT
    TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.lfg_listings l
            WHERE l.user_id = player_game_stats.user_id
              AND l.status = 'published'
        )
        OR EXISTS (
            SELECT 1 FROM public.player_game_profiles pgp
            WHERE pgp.user_id = player_game_stats.user_id
              AND pgp.profile_visible = TRUE
        )
    );

DROP POLICY IF EXISTS pgs_service ON public.player_game_stats;
CREATE POLICY pgs_service
    ON public.player_game_stats
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);
