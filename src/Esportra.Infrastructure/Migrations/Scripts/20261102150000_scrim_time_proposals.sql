-- Time proposals for scrim sessions.
--
-- A scrim is created when an offer is accepted, with no time. Either team's captain/manager then
-- proposes a time and the other team accepts or declines (or counters by proposing again).
-- Accepting a proposal sets scrim_sessions.scheduled_at; the API writes it in one transaction.

CREATE TABLE IF NOT EXISTS public.scrim_time_proposals (
    id                  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id          UUID        NOT NULL REFERENCES public.scrim_sessions (id) ON DELETE CASCADE,
    proposed_by_team_id UUID        NOT NULL REFERENCES public.teams (id) ON DELETE CASCADE,
    proposed_at         TIMESTAMPTZ NOT NULL,
    note                TEXT        NULL,
    status              TEXT        NOT NULL DEFAULT 'pending',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    responded_at        TIMESTAMPTZ NULL,
    responded_by        UUID        NULL
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_time_proposals_status') THEN
        ALTER TABLE public.scrim_time_proposals
            ADD CONSTRAINT chk_scrim_time_proposals_status
            CHECK (status IN ('pending', 'accepted', 'declined', 'superseded'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_time_proposals_note_len') THEN
        ALTER TABLE public.scrim_time_proposals
            ADD CONSTRAINT chk_scrim_time_proposals_note_len
            CHECK (note IS NULL OR char_length(note) <= 200);
    END IF;
END $$;

-- At most one pending proposal per session: a new proposal supersedes the old one.
CREATE UNIQUE INDEX IF NOT EXISTS uq_scrim_time_proposals_one_pending
    ON public.scrim_time_proposals (session_id) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS idx_scrim_time_proposals_session
    ON public.scrim_time_proposals (session_id, created_at DESC);

ALTER TABLE public.scrim_time_proposals ENABLE ROW LEVEL SECURITY;

-- Staff of either team may read; all writes go through the API (service role).
DROP POLICY IF EXISTS scrim_time_proposals_teams_read ON public.scrim_time_proposals;
CREATE POLICY scrim_time_proposals_teams_read
    ON public.scrim_time_proposals FOR SELECT TO authenticated
    USING (
        EXISTS (
            SELECT 1
            FROM public.scrim_sessions ss
            JOIN public.team_members tm ON tm.team_id IN (ss.team1_id, ss.team2_id)
            WHERE ss.id = scrim_time_proposals.session_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

DROP POLICY IF EXISTS scrim_time_proposals_service ON public.scrim_time_proposals;
CREATE POLICY scrim_time_proposals_service
    ON public.scrim_time_proposals FOR ALL TO service_role
    USING (true) WITH CHECK (true);
