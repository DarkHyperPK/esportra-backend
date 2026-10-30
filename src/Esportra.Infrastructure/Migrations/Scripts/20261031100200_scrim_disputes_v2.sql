-- PROJ-078 T2: Scrim disputes v2 fields + admin RLS policy.
-- Adds evidence submission, responder evidence, resolved_by, outcome, resolution_notes
-- to support the full staff dispute resolution flow.
-- Also adds an admin RLS policy so staff with disputes:resolve permission can read
-- and update all scrim disputes (not just those in their own sessions).

ALTER TABLE public.scrim_disputes
    ADD COLUMN IF NOT EXISTS evidence_urls           TEXT[]  NULL,
    ADD COLUMN IF NOT EXISTS responder_evidence_urls TEXT[]  NULL,
    ADD COLUMN IF NOT EXISTS responder_notes         TEXT    NULL,
    ADD COLUMN IF NOT EXISTS resolved_by             UUID    NULL
        REFERENCES auth.users(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS outcome                 TEXT    NULL,
    ADD COLUMN IF NOT EXISTS resolution_notes        TEXT    NULL;

-- outcome constraint: only accepted values
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_dispute_outcome') THEN
        ALTER TABLE public.scrim_disputes
            ADD CONSTRAINT chk_scrim_dispute_outcome
            CHECK (outcome IS NULL OR outcome IN ('team1_wins', 'team2_wins', 'no_contest'));
    END IF;
END $$;

-- Admin RLS policy: ops_admin and moderator can read and update all scrim disputes.
-- Uses admin_user_roles join (same pattern as dispute_comments RLS in 20260304000008).
DROP POLICY IF EXISTS scrim_disputes_staff ON public.scrim_disputes;
CREATE POLICY scrim_disputes_staff
    ON public.scrim_disputes FOR ALL TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.admin_user_roles aur
            WHERE aur.user_id = auth.uid()
        )
    )
    WITH CHECK (
        EXISTS (
            SELECT 1 FROM public.admin_user_roles aur
            WHERE aur.user_id = auth.uid()
        )
    );
