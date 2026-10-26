-- PROJ-076 Phase 3: User-declared preferred agents on LFG listings.
-- Separate from player_game_stats (auto-fetched Riot data) — this is self-reported.

ALTER TABLE public.lfg_listings
    ADD COLUMN IF NOT EXISTS preferred_agents TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[];

CREATE INDEX IF NOT EXISTS idx_lfg_preferred_agents
    ON public.lfg_listings USING GIN (preferred_agents);
