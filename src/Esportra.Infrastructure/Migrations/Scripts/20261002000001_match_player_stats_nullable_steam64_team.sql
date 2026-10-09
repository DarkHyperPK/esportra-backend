-- steam64_id and team are MatchZy-specific and not available for companion-sourced stats.
-- Make them nullable so Valorant companion rows can be inserted without a Steam identity.
ALTER TABLE public.match_player_stats
    ALTER COLUMN steam64_id DROP NOT NULL,
    ALTER COLUMN team       DROP NOT NULL;
