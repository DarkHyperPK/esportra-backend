-- 1. Create match_disputes table (referenced by useMatchDispute and useMatchResultReport
--    but never existed in the DB).

CREATE TABLE IF NOT EXISTS public.match_disputes (
    id                    UUID DEFAULT gen_random_uuid() PRIMARY KEY,
    match_id              UUID NOT NULL,
    disputed_by_team_id   UUID REFERENCES public.teams(id) ON DELETE SET NULL,
    disputed_by_user_id   UUID REFERENCES public.profiles(id) ON DELETE SET NULL,
    reason                TEXT NOT NULL,
    evidence_urls         TEXT[] DEFAULT '{}',
    status                TEXT NOT NULL DEFAULT 'pending'
        CONSTRAINT match_disputes_status_check CHECK (status IN ('pending', 'resolved', 'rejected')),
    resolution            TEXT,
    resolved_at           TIMESTAMPTZ,
    resolved_by           UUID REFERENCES public.profiles(id) ON DELETE SET NULL,
    created_at            TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE public.match_disputes ENABLE ROW LEVEL SECURITY;

-- Participants in the match can insert disputes
CREATE POLICY "Participants can file disputes" ON public.match_disputes
    FOR INSERT WITH CHECK (disputed_by_user_id = auth.uid());

-- Anyone involved can view disputes for their match
CREATE POLICY "Participants and organizers can view disputes" ON public.match_disputes
    FOR SELECT USING (true);

-- Only organizers/admins can update (resolve/reject)
CREATE POLICY "Organizers can resolve disputes" ON public.match_disputes
    FOR UPDATE USING (
        EXISTS (
            SELECT 1 FROM public.profiles
            WHERE id = auth.uid()
              AND (is_admin = true OR role = 'admin')
        )
        OR EXISTS (
            SELECT 1 FROM public.brkt_matches m
            JOIN public.brkt_versions v ON m.version_id = v.id
            JOIN public.tournaments t ON v.tournament_id = t.id
            WHERE m.id = match_disputes.match_id
              AND t.organizer_id = auth.uid()
        )
    );

-- 2. Fix match_result_reports UPDATE policy.
--    The current policy only allows service_role and organizers.
--    Opposing captains must be able to mark a report as 'disputed'.
--    We extend the policy to also allow participants (team captains/members)
--    who are part of the match but did NOT submit the original report.

DROP POLICY IF EXISTS match_result_reports_update ON public.match_result_reports;

CREATE POLICY match_result_reports_update ON public.match_result_reports
    FOR UPDATE USING (
        -- Service role (edge functions)
        (SELECT auth.role()) = 'service_role'
        OR
        -- Tournament organizer
        EXISTS (
            SELECT 1 FROM public.brkt_matches m
            JOIN public.brkt_versions v ON m.version_id = v.id
            JOIN public.tournaments t ON v.tournament_id = t.id
            WHERE m.id = match_result_reports.match_id
              AND t.organizer_id = auth.uid()
        )
        OR
        -- Opposing captain: authenticated, did not file the report,
        -- and is a participant in the tournament that owns this match
        (
            auth.uid() IS NOT NULL
            AND auth.uid() != match_result_reports.reported_by
            AND EXISTS (
                SELECT 1 FROM public.brkt_matches m
                JOIN public.brkt_versions v ON m.version_id = v.id
                JOIN public.tournament_participants tp ON tp.tournament_id = v.tournament_id
                WHERE m.id = match_result_reports.match_id
                  AND (
                    tp.user_id = auth.uid()
                    OR tp.team_id IN (
                        SELECT team_id FROM public.team_members
                        WHERE user_id = auth.uid() AND is_active = true
                    )
                  )
            )
        )
    );
