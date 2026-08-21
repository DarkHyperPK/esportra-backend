-- Cash payout lifecycle per team per tournament.
-- One row per team when their prize_amount > 0 (seeded on placement resolution).
-- 'manual' = organizer handles payment outside the platform.
-- 'gateway' = future automated payout; gateway fields remain NULL until integration is wired.
-- Uses the existing payout_status enum: requested, approved, rejected, paid, failed.

CREATE TABLE IF NOT EXISTS public.tournament_cash_payouts (
    id                   UUID DEFAULT gen_random_uuid() PRIMARY KEY,
    tournament_id        UUID NOT NULL REFERENCES public.tournaments(id) ON DELETE CASCADE,
    team_id              UUID NOT NULL,
    placement            INTEGER NOT NULL,
    amount               NUMERIC(10,2) NOT NULL,
    currency             TEXT NOT NULL DEFAULT 'USD',
    payment_method       TEXT NOT NULL DEFAULT 'manual'
        CHECK (payment_method IN ('gateway', 'manual')),
    manual_payment_notes TEXT,
    status               payout_status NOT NULL DEFAULT 'requested',
    -- Gateway fields (NULL until payment gateway is integrated):
    gateway_reference    TEXT,
    gateway_response     JSONB,
    initiated_by         UUID,
    initiated_at         TIMESTAMPTZ,
    paid_at              TIMESTAMPTZ,
    failed_reason        TEXT,
    created_at           TIMESTAMPTZ DEFAULT NOW(),
    updated_at           TIMESTAMPTZ DEFAULT NOW(),
    CONSTRAINT uq_tournament_cash_payout_team UNIQUE (tournament_id, team_id)
);

CREATE INDEX IF NOT EXISTS idx_tournament_cash_payouts_tournament
    ON public.tournament_cash_payouts(tournament_id, status);

ALTER TABLE public.tournament_cash_payouts ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.tournament_cash_payouts FORCE ROW LEVEL SECURITY;

REVOKE ALL ON public.tournament_cash_payouts FROM anon, authenticated;
GRANT ALL ON public.tournament_cash_payouts TO service_role;

DROP POLICY IF EXISTS tournament_cash_payouts_service_role_all ON public.tournament_cash_payouts;
CREATE POLICY tournament_cash_payouts_service_role_all ON public.tournament_cash_payouts
    FOR ALL TO service_role USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS tournament_cash_payouts_organizer_read ON public.tournament_cash_payouts;
CREATE POLICY tournament_cash_payouts_organizer_read ON public.tournament_cash_payouts
    FOR SELECT TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.tournaments t
            WHERE t.id = tournament_id
              AND (t.organizer_id = auth.uid() OR t.is_public = true)
        )
    );
