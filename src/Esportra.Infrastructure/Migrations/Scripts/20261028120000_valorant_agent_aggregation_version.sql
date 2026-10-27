-- Existing aggregates may have incomplete per-agent counters; rebuild on the next sync.
ALTER TABLE public.player_game_stats
    ADD COLUMN IF NOT EXISTS aggregation_version integer NOT NULL DEFAULT 0;
