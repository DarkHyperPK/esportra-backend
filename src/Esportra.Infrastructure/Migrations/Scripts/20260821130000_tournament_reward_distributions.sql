-- Tracks fulfillment status of organizer-managed rewards (physical products,
-- in-game currencies, digital goods) per team per placement.
-- Cash prizes are tracked only in tournament_placements.prize_amount.
-- The platform is NOT responsible for fulfillment of organizer-managed rewards.

CREATE TABLE IF NOT EXISTS public.tournament_reward_distributions (
    id              UUID DEFAULT gen_random_uuid() PRIMARY KEY,
    tournament_id   UUID NOT NULL REFERENCES public.tournaments(id) ON DELETE CASCADE,
    team_id         UUID NOT NULL,
    placement       INTEGER NOT NULL,
    reward_index    INTEGER NOT NULL,  -- 0-based index into placement band's rewards array
    reward_title    TEXT NOT NULL,
    reward_type     TEXT NOT NULL,
    status          TEXT NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'distributed', 'claimed', 'cancelled')),
    notes           TEXT,
    distributed_by  UUID,
    distributed_at  TIMESTAMPTZ,
    created_at      TIMESTAMPTZ DEFAULT NOW(),
    updated_at      TIMESTAMPTZ DEFAULT NOW(),
    CONSTRAINT uq_tournament_reward_dist UNIQUE (tournament_id, team_id, reward_index)
);

CREATE INDEX IF NOT EXISTS idx_tournament_reward_dist_tournament
    ON public.tournament_reward_distributions(tournament_id, placement);

ALTER TABLE public.tournament_reward_distributions ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.tournament_reward_distributions FORCE ROW LEVEL SECURITY;

REVOKE ALL ON public.tournament_reward_distributions FROM anon, authenticated;
GRANT ALL ON public.tournament_reward_distributions TO service_role;

DROP POLICY IF EXISTS tournament_reward_dist_service_role_all ON public.tournament_reward_distributions;
CREATE POLICY tournament_reward_dist_service_role_all ON public.tournament_reward_distributions
    FOR ALL TO service_role USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS tournament_reward_dist_public_read ON public.tournament_reward_distributions;
CREATE POLICY tournament_reward_dist_public_read ON public.tournament_reward_distributions
    FOR SELECT TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.tournaments t
            WHERE t.id = tournament_id
              AND (t.is_public = true OR t.organizer_id = auth.uid())
        )
    );
