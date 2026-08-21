-- Prize distribution system: tournament_placements table
-- The prize_distribution jsonb column already exists on tournaments (default '[]').
-- This migration materializes resolved final placements after tournament completion.

CREATE TABLE IF NOT EXISTS public.tournament_placements (
    id              UUID DEFAULT gen_random_uuid() PRIMARY KEY,
    tournament_id   UUID NOT NULL REFERENCES public.tournaments(id) ON DELETE CASCADE,
    team_id         UUID NOT NULL,
    placement       INTEGER NOT NULL,
    placement_label TEXT NOT NULL,
    prize_amount    NUMERIC(10,2) DEFAULT 0,
    prize_rewards   JSONB DEFAULT '[]'::jsonb,
    is_tied         BOOLEAN DEFAULT FALSE,
    resolved_at     TIMESTAMPTZ DEFAULT NOW(),
    created_at      TIMESTAMPTZ DEFAULT NOW(),
    CONSTRAINT uq_tournament_placements_team UNIQUE (tournament_id, team_id)
);

CREATE INDEX IF NOT EXISTS idx_tournament_placements_tournament
    ON public.tournament_placements(tournament_id, placement);

ALTER TABLE public.tournament_placements ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.tournament_placements FORCE ROW LEVEL SECURITY;

REVOKE ALL ON public.tournament_placements FROM anon, authenticated;
GRANT ALL ON public.tournament_placements TO service_role;

DROP POLICY IF EXISTS tournament_placements_service_role_all ON public.tournament_placements;
CREATE POLICY tournament_placements_service_role_all ON public.tournament_placements
    FOR ALL TO service_role USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS tournament_placements_public_read ON public.tournament_placements;
CREATE POLICY tournament_placements_public_read ON public.tournament_placements
    FOR SELECT TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.tournaments t
            WHERE t.id = tournament_id
              AND (t.is_public = true OR t.organizer_id = auth.uid())
        )
    );
