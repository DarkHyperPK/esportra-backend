-- Canonical tournament invitation schema and tournament registration source tracking.

ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'tournament_invite';

CREATE TABLE IF NOT EXISTS public.tournament_invitations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tournament_id UUID NOT NULL REFERENCES public.tournaments(id) ON DELETE CASCADE,
    email TEXT NOT NULL,
    code TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'draft',
    expires_at TIMESTAMPTZ,
    redeemed_by UUID REFERENCES public.profiles(id) ON DELETE SET NULL,
    redeemed_team_id UUID REFERENCES public.teams(id) ON DELETE SET NULL,
    redeemed_at TIMESTAMPTZ,
    sent_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_tournament_invitations_code UNIQUE (code),
    CONSTRAINT chk_tournament_invitations_status CHECK (status IN ('draft','sent','redeemed','expired','revoked')),
    CONSTRAINT chk_tournament_invitations_email_normalized CHECK (email = lower(trim(email))),
    CONSTRAINT chk_tournament_invitations_expires_after_create CHECK (expires_at IS NULL OR expires_at > created_at),
    CONSTRAINT chk_tournament_invitations_redeemed_fields CHECK (
        (status = 'redeemed' AND redeemed_at IS NOT NULL AND redeemed_by IS NOT NULL AND redeemed_team_id IS NOT NULL)
        OR (status <> 'redeemed' AND redeemed_at IS NULL)
    )
);

CREATE INDEX IF NOT EXISTS idx_tournament_invitations_tournament_id ON public.tournament_invitations(tournament_id);
CREATE INDEX IF NOT EXISTS idx_tournament_invitations_code_lower ON public.tournament_invitations(LOWER(code));
CREATE INDEX IF NOT EXISTS idx_tournament_invitations_email_lower ON public.tournament_invitations(LOWER(email));
CREATE INDEX IF NOT EXISTS idx_tournament_invitations_status_expires ON public.tournament_invitations(status, expires_at);
CREATE UNIQUE INDEX IF NOT EXISTS ux_tournament_invitation_active_email ON public.tournament_invitations(tournament_id, LOWER(email)) WHERE status <> 'revoked';

ALTER TABLE public.tournaments ADD COLUMN IF NOT EXISTS reserved_invite_slots INT NOT NULL DEFAULT 0;
ALTER TABLE public.tournaments ADD COLUMN IF NOT EXISTS invite_expiry_days INT NOT NULL DEFAULT 7;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_tournaments_reserved_invite_slots_nonnegative') THEN
        ALTER TABLE public.tournaments
            ADD CONSTRAINT chk_tournaments_reserved_invite_slots_nonnegative CHECK (reserved_invite_slots >= 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_tournaments_invite_expiry_days_positive') THEN
        ALTER TABLE public.tournaments
            ADD CONSTRAINT chk_tournaments_invite_expiry_days_positive CHECK (invite_expiry_days BETWEEN 1 AND 365);
    END IF;
END;
$$;

ALTER TABLE public.tournament_participants ADD COLUMN IF NOT EXISTS source TEXT NOT NULL DEFAULT 'open';
UPDATE public.tournament_participants SET source = 'open' WHERE source = 'auto_qualified';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_tournament_participants_source') THEN
        ALTER TABLE public.tournament_participants
            ADD CONSTRAINT chk_tournament_participants_source CHECK (source IN ('open','invite','advancement'));
    END IF;
END;
$$;

CREATE INDEX IF NOT EXISTS idx_tournament_participants_source ON public.tournament_participants(tournament_id, source);
