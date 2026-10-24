-- PROJ-065 Competitive Phase 1: player game profiles
-- One row per (user_id, game_slug); stores rank, roles, region and visibility flags.

-- ── Table ─────────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.player_game_profiles (
    id                       UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id                  UUID        NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    game_slug                TEXT        NOT NULL,
    rank_tier_key            TEXT,
    rank_source              TEXT        NOT NULL DEFAULT 'self_reported',
    rank_verified_at         TIMESTAMPTZ,
    preferred_roles          TEXT[]      NOT NULL DEFAULT ARRAY[]::TEXT[],
    main_role                TEXT,
    region_key               TEXT,
    preferred_servers        TEXT[]      NOT NULL DEFAULT ARRAY[]::TEXT[],
    looking_for_team         BOOLEAN     NOT NULL DEFAULT FALSE,
    recruitment_visible      BOOLEAN     NOT NULL DEFAULT FALSE,
    profile_visible          BOOLEAN     NOT NULL DEFAULT FALSE,
    availability_visible     BOOLEAN     NOT NULL DEFAULT FALSE,
    riot_id_visible          BOOLEAN     NOT NULL DEFAULT FALSE,
    competitive_experience   TEXT,
    preferred_team_environment TEXT,
    languages                TEXT[]      NOT NULL DEFAULT ARRAY[]::TEXT[],
    notes                    TEXT,
    created_at               TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at               TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ── Named unique constraint (guarded) ─────────────────────────────────────────

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'pgp_user_game_unique'
    ) THEN
        ALTER TABLE public.player_game_profiles
            ADD CONSTRAINT pgp_user_game_unique UNIQUE (user_id, game_slug);
    END IF;
END $$;

-- ── Indexes ───────────────────────────────────────────────────────────────────

CREATE INDEX IF NOT EXISTS idx_pgp_user
    ON public.player_game_profiles (user_id);

CREATE INDEX IF NOT EXISTS idx_pgp_game
    ON public.player_game_profiles (game_slug);

-- Partial index: discovery of players actively looking for a team
CREATE INDEX IF NOT EXISTS idx_pgp_lft
    ON public.player_game_profiles (game_slug)
    WHERE looking_for_team = TRUE AND profile_visible = TRUE;

-- ── RLS ───────────────────────────────────────────────────────────────────────

ALTER TABLE public.player_game_profiles ENABLE ROW LEVEL SECURITY;

-- Owner: full access to own rows
DROP POLICY IF EXISTS pgp_own ON public.player_game_profiles;
CREATE POLICY pgp_own
    ON public.player_game_profiles
    FOR ALL
    TO authenticated
    USING  (user_id = auth.uid())
    WITH CHECK (user_id = auth.uid());

-- Public read: authenticated users may read visible profiles
DROP POLICY IF EXISTS pgp_public ON public.player_game_profiles;
CREATE POLICY pgp_public
    ON public.player_game_profiles
    FOR SELECT
    TO authenticated
    USING (profile_visible = TRUE);

-- Service role: unrestricted (backend RPCs and admin tooling)
DROP POLICY IF EXISTS pgp_service ON public.player_game_profiles;
CREATE POLICY pgp_service
    ON public.player_game_profiles
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);
