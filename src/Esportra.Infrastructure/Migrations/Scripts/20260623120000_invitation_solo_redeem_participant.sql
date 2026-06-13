-- Solo invite redemption stores tournament_participants.id, not teams.id.

ALTER TABLE public.tournament_invitations
    ADD COLUMN IF NOT EXISTS redeemed_participant_id UUID
        REFERENCES public.tournament_participants(id) ON DELETE SET NULL;

ALTER TABLE public.tournament_invitations
    DROP CONSTRAINT IF EXISTS chk_tournament_invitations_redeemed_fields;

ALTER TABLE public.tournament_invitations
    ADD CONSTRAINT chk_tournament_invitations_redeemed_fields CHECK (
        (
            status = 'redeemed'
            AND redeemed_at IS NOT NULL
            AND redeemed_by IS NOT NULL
            AND (redeemed_team_id IS NOT NULL OR redeemed_participant_id IS NOT NULL)
        )
        OR (status <> 'redeemed' AND redeemed_at IS NULL)
    );

CREATE INDEX IF NOT EXISTS idx_tournament_invitations_redeemed_participant_id
    ON public.tournament_invitations(redeemed_participant_id)
    WHERE redeemed_participant_id IS NOT NULL;
