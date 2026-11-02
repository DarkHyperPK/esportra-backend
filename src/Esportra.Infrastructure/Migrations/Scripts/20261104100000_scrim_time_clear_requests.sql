-- Clearing an agreed scrim time is a mutual decision: one team asks, the other agrees.
CREATE TABLE IF NOT EXISTS public.scrim_time_clear_requests (
    id                    UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id            UUID        NOT NULL REFERENCES public.scrim_sessions(id) ON DELETE CASCADE,
    requested_by_team_id  UUID        NOT NULL REFERENCES public.teams(id) ON DELETE CASCADE,
    status                TEXT        NOT NULL DEFAULT 'pending',
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    responded_at          TIMESTAMPTZ NULL,
    responded_by          UUID        NULL REFERENCES auth.users(id) ON DELETE SET NULL
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_time_clear_status') THEN
        ALTER TABLE public.scrim_time_clear_requests
            ADD CONSTRAINT chk_scrim_time_clear_status
            CHECK (status IN ('pending', 'accepted', 'declined', 'withdrawn', 'superseded'));
    END IF;
END $$;

-- At most one open request per scrim.
CREATE UNIQUE INDEX IF NOT EXISTS uq_scrim_time_clear_pending
    ON public.scrim_time_clear_requests (session_id) WHERE status = 'pending';

ALTER TABLE public.scrim_time_clear_requests ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS scrim_time_clear_requests_teams_read ON public.scrim_time_clear_requests;
CREATE POLICY scrim_time_clear_requests_teams_read
    ON public.scrim_time_clear_requests FOR SELECT TO authenticated
    USING (
        EXISTS (
            SELECT 1
            FROM public.scrim_sessions ss
            JOIN public.team_members tm ON tm.team_id IN (ss.team1_id, ss.team2_id)
            WHERE ss.id = scrim_time_clear_requests.session_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

DROP POLICY IF EXISTS scrim_time_clear_requests_service ON public.scrim_time_clear_requests;
CREATE POLICY scrim_time_clear_requests_service
    ON public.scrim_time_clear_requests FOR ALL TO service_role
    USING (true) WITH CHECK (true);
