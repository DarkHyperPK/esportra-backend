-- PROJ-076 Phase 2: add team_id to lfg_listings for LFP (team posting listings)
-- Nullable — individual LFT listings leave this NULL.

ALTER TABLE public.lfg_listings
    ADD COLUMN IF NOT EXISTS team_id UUID REFERENCES public.teams(id) ON DELETE SET NULL;

CREATE INDEX IF NOT EXISTS idx_lfg_team
    ON public.lfg_listings (team_id)
    WHERE team_id IS NOT NULL;
