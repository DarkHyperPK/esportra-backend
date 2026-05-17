-- Staging repair for tournament invitation schema already created by retired experimental migrations.
-- Safe on production after the canonical migration.

ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'tournament_invite';

ALTER TABLE public.tournaments ADD COLUMN IF NOT EXISTS reserved_invite_slots INT NOT NULL DEFAULT 0;
ALTER TABLE public.tournaments ADD COLUMN IF NOT EXISTS invite_expiry_days INT NOT NULL DEFAULT 7;
ALTER TABLE public.tournament_participants ADD COLUMN IF NOT EXISTS source TEXT NOT NULL DEFAULT 'open';
UPDATE public.tournament_participants SET source = 'open' WHERE source = 'auto_qualified';

ALTER TABLE public.tournament_invitations ADD COLUMN IF NOT EXISTS sent_at TIMESTAMPTZ;
ALTER TABLE public.tournament_invitations ADD COLUMN IF NOT EXISTS redeemed_by UUID REFERENCES public.profiles(id) ON DELETE SET NULL;
ALTER TABLE public.tournament_invitations ADD COLUMN IF NOT EXISTS redeemed_team_id UUID REFERENCES public.teams(id) ON DELETE SET NULL;
ALTER TABLE public.tournament_invitations ADD COLUMN IF NOT EXISTS redeemed_at TIMESTAMPTZ;
ALTER TABLE public.tournament_invitations ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

UPDATE public.tournament_invitations SET email = lower(trim(email)) WHERE email <> lower(trim(email));

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
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_tournament_participants_source') THEN
        ALTER TABLE public.tournament_participants DROP CONSTRAINT chk_tournament_participants_source;
    END IF;
    ALTER TABLE public.tournament_participants
        ADD CONSTRAINT chk_tournament_participants_source CHECK (source IN ('open','invite','advancement'));
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_tournament_invitations_code ON public.tournament_invitations(code);
CREATE INDEX IF NOT EXISTS idx_tournament_invitations_tournament_id ON public.tournament_invitations(tournament_id);
CREATE INDEX IF NOT EXISTS idx_tournament_invitations_code_lower ON public.tournament_invitations(LOWER(code));
CREATE INDEX IF NOT EXISTS idx_tournament_invitations_email_lower ON public.tournament_invitations(LOWER(email));
CREATE INDEX IF NOT EXISTS idx_tournament_invitations_status_expires ON public.tournament_invitations(status, expires_at);
CREATE UNIQUE INDEX IF NOT EXISTS ux_tournament_invitation_active_email ON public.tournament_invitations(tournament_id, LOWER(email)) WHERE status <> 'revoked';
CREATE INDEX IF NOT EXISTS idx_tournament_participants_source ON public.tournament_participants(tournament_id, source);

ALTER TABLE public.tournament_invitations ENABLE ROW LEVEL SECURITY;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.tournament_invitations TO service_role;
REVOKE INSERT, UPDATE, DELETE ON public.tournament_invitations FROM authenticated;
GRANT SELECT ON public.tournament_invitations TO authenticated;
GRANT SELECT, INSERT, UPDATE ON public.tournaments TO service_role;
GRANT SELECT, INSERT, UPDATE ON public.tournament_participants TO service_role;
GRANT SELECT ON public.tournament_participants TO authenticated;

DROP POLICY IF EXISTS tournament_invitations_service_role_all ON public.tournament_invitations;
CREATE POLICY tournament_invitations_service_role_all ON public.tournament_invitations
    FOR ALL TO service_role USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS tournament_invitations_authenticated_read_own ON public.tournament_invitations;
CREATE POLICY tournament_invitations_authenticated_read_own ON public.tournament_invitations
    FOR SELECT TO authenticated
    USING (lower(email) = lower(coalesce(auth.jwt() ->> 'email', '')));
