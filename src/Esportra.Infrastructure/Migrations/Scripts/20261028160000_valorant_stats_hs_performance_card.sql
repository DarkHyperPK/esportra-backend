-- Extend Valorant stats with headshot%, performance score, and player card.
-- Also adds HS columns to the match archive so incremental syncs aggregate correctly.
ALTER TABLE public.player_game_stats
    ADD COLUMN IF NOT EXISTS total_headshots       INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS total_shots           INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS total_performance_sum NUMERIC NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS hs_pct                NUMERIC,
    ADD COLUMN IF NOT EXISTS avg_performance       NUMERIC,
    ADD COLUMN IF NOT EXISTS player_card_id        TEXT;

ALTER TABLE public.valorant_player_matches
    ADD COLUMN IF NOT EXISTS headshots  INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS shots      INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS performance NUMERIC NOT NULL DEFAULT 0;
