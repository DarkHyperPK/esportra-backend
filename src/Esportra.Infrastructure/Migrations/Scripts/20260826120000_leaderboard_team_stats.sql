-- ============================================================================
-- Migration: Leaderboard Team Stats Table
-- Purpose:   Persisted grain (team_id × game_key × region_key) team ranking
--            stats powering the public /leaderboards system.
--
--            Written exclusively by LeaderboardStatsService.RecomputeAsync
--            (full recompute, transactional DELETE+INSERT) driven by the
--            Hangfire "leaderboard-refresh" recurring job. Never mutated by
--            request paths.
--
-- Grain semantics:
--   game_key   — normalized via game_catalog_game_aliases (fallback:
--                lower(trim(tournament.game))) so catalog variants unify.
--   region_key — lower(trim(tournaments.region)); 'global' when unset/empty.
--                A team playing across regions earns separate regional rows;
--                global rankings aggregate across them at read time.
-- ============================================================================

CREATE TABLE IF NOT EXISTS public.leaderboard_team_stats (
    team_id             UUID        NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    game_key            TEXT        NOT NULL,
    region_key          TEXT        NOT NULL DEFAULT 'global',
    matches_played      INT         NOT NULL DEFAULT 0,
    wins                INT         NOT NULL DEFAULT 0,
    losses              INT         NOT NULL DEFAULT 0,
    tournaments_played  INT         NOT NULL DEFAULT 0,
    tournaments_won     INT         NOT NULL DEFAULT 0,
    placement_points    INT         NOT NULL DEFAULT 0,
    best_placement      INT,
    rp                  BIGINT      NOT NULL DEFAULT 0,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT pk_leaderboard_team_stats PRIMARY KEY (team_id, game_key, region_key),
    CONSTRAINT chk_leaderboard_team_stats_non_negative
        CHECK (matches_played >= 0 AND wins >= 0 AND losses >= 0
               AND wins + losses <= matches_played
               AND tournaments_played >= 0 AND tournaments_won >= 0)
);

CREATE INDEX IF NOT EXISTS idx_leaderboard_team_stats_game_region_rp
    ON public.leaderboard_team_stats (game_key, region_key, rp DESC);

CREATE INDEX IF NOT EXISTS idx_leaderboard_team_stats_game_rp
    ON public.leaderboard_team_stats (game_key, rp DESC);
