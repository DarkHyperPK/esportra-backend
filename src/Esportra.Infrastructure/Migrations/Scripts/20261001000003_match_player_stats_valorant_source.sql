ALTER TABLE match_player_stats
    ADD COLUMN IF NOT EXISTS stat_source  text CHECK (stat_source IN ('matchzy','companion','riot_api','organizer_reported')),
    ADD COLUMN IF NOT EXISTS riot_match_id text,
    ADD COLUMN IF NOT EXISTS verified     boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS extra_data   jsonb;  -- Valorant-specific fields (agent, map, outcome, round_stats, collected_at)

-- Partial unique index for companion / Riot-API stat upserts.
-- Allows ON CONFLICT (match_id, riot_match_id, user_id) WHERE riot_match_id IS NOT NULL AND user_id IS NOT NULL.
CREATE UNIQUE INDEX IF NOT EXISTS match_player_stats_companion_uniq
    ON match_player_stats (match_id, riot_match_id, user_id)
    WHERE riot_match_id IS NOT NULL AND user_id IS NOT NULL;

-- PRE-FLIGHT: Run on production before deploying, confirm expected count before proceeding:
-- SELECT count(*) FROM match_player_stats WHERE stat_source IS NULL;
-- Expected: all existing CS2 rows (inserted by MatchZy; backfill is safe)
UPDATE match_player_stats SET stat_source = 'matchzy' WHERE stat_source IS NULL;
