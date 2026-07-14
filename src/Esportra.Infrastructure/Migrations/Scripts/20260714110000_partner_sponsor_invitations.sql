CREATE TABLE IF NOT EXISTS public.partner_sponsor_invitations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    sponsor_id UUID NOT NULL REFERENCES public.sponsors(id) ON DELETE CASCADE,
    email TEXT NOT NULL,
    role TEXT NOT NULL DEFAULT 'owner' CHECK (role IN ('owner', 'viewer')),
    requires_password_setup BOOLEAN NOT NULL DEFAULT FALSE,
    token_hash TEXT NOT NULL UNIQUE,
    status TEXT NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'accepted', 'revoked', 'delivery_failed')),
    invited_by UUID NOT NULL REFERENCES auth.users(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ NOT NULL,
    accepted_at TIMESTAMPTZ,
    revoked_at TIMESTAMPTZ
);

ALTER TABLE public.partner_sponsor_invitations
    ADD COLUMN IF NOT EXISTS requires_password_setup BOOLEAN NOT NULL DEFAULT FALSE;

CREATE UNIQUE INDEX IF NOT EXISTS partner_sponsor_invitations_active_email_key
    ON public.partner_sponsor_invitations (LOWER(email))
    WHERE status = 'pending';

CREATE INDEX IF NOT EXISTS partner_sponsor_invitations_token_hash_idx
    ON public.partner_sponsor_invitations (token_hash);

CREATE INDEX IF NOT EXISTS partner_sponsor_invitations_sponsor_status_idx
    ON public.partner_sponsor_invitations (sponsor_id, status);

ALTER TABLE public.partner_sponsor_invitations ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS partner_sponsor_invitations_service_role_all
    ON public.partner_sponsor_invitations;
CREATE POLICY partner_sponsor_invitations_service_role_all
    ON public.partner_sponsor_invitations
    FOR ALL TO service_role
    USING (true)
    WITH CHECK (true);

GRANT ALL ON public.partner_sponsor_invitations TO service_role;

ALTER TABLE public.sponsor_accounts
    ADD COLUMN IF NOT EXISTS onboarding_meta JSONB NOT NULL
        DEFAULT '{"completed":false,"current_step":0,"steps":{}}'::jsonb;

ALTER TABLE public.sponsor_accounts ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.sponsor_accounts FORCE ROW LEVEL SECURITY;
REVOKE ALL ON public.sponsor_accounts FROM anon, authenticated;
DROP POLICY IF EXISTS sponsor_accounts_service_role_all ON public.sponsor_accounts;
CREATE POLICY sponsor_accounts_service_role_all
    ON public.sponsor_accounts
    FOR ALL TO service_role
    USING (true)
    WITH CHECK (true);
GRANT ALL ON public.sponsor_accounts TO service_role;

ALTER TABLE public.partner_applications ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.partner_applications FORCE ROW LEVEL SECURITY;
REVOKE ALL ON public.partner_applications FROM anon, authenticated;
DROP POLICY IF EXISTS partner_applications_service_role_all ON public.partner_applications;
CREATE POLICY partner_applications_service_role_all
    ON public.partner_applications
    FOR ALL TO service_role
    USING (true)
    WITH CHECK (true);
GRANT ALL ON public.partner_applications TO service_role;