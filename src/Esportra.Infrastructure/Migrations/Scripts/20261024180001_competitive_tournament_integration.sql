-- PROJ-069 Competitive Phase 5: Tournament-Competitive Integration
-- A. competitive_eligible column on tournament_participants
-- B. roster_snapshot JSONB on brkt_matches
-- No data changes; purely additive.

-- A. competitive_eligible on tournament_participants
ALTER TABLE public.tournament_participants
    ADD COLUMN IF NOT EXISTS competitive_eligible BOOLEAN;

-- B. roster_snapshot on brkt_matches (JSONB snapshot of active roster at match start)
ALTER TABLE public.brkt_matches
    ADD COLUMN IF NOT EXISTS roster_snapshot JSONB;
