ALTER TABLE public.partner_sponsor_invitations
    ADD COLUMN IF NOT EXISTS accepted_by_user_id UUID REFERENCES auth.users(id),
    ADD COLUMN IF NOT EXISTS delivered_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS delivery_attempts INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS delivery_error_code TEXT,
    ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

ALTER TABLE public.partner_sponsor_invitations
    DROP CONSTRAINT IF EXISTS partner_sponsor_invitations_status_check;
ALTER TABLE public.partner_sponsor_invitations
    ADD CONSTRAINT partner_sponsor_invitations_status_check
    CHECK (status IN ('pending', 'accepted', 'revoked', 'delivery_failed', 'expired'));

DROP INDEX IF EXISTS public.partner_sponsor_invitations_active_email_key;
CREATE UNIQUE INDEX IF NOT EXISTS partner_sponsor_invitations_active_sponsor_email_key
    ON public.partner_sponsor_invitations (sponsor_id, LOWER(email))
    WHERE status = 'pending';

CREATE INDEX IF NOT EXISTS partner_sponsor_invitations_email_status_idx
    ON public.partner_sponsor_invitations (LOWER(email), status);

ALTER TABLE public.partner_sponsor_invitations ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.partner_sponsor_invitations FORCE ROW LEVEL SECURITY;
REVOKE ALL ON public.partner_sponsor_invitations FROM anon, authenticated;
GRANT ALL ON public.partner_sponsor_invitations TO service_role;

ALTER TABLE public.sponsor_accounts
    ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'active',
    ADD COLUMN IF NOT EXISTS invitation_id UUID REFERENCES public.partner_sponsor_invitations(id),
    ADD COLUMN IF NOT EXISTS accepted_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

ALTER TABLE public.sponsor_accounts
    DROP CONSTRAINT IF EXISTS sponsor_accounts_status_check;
ALTER TABLE public.sponsor_accounts
    ADD CONSTRAINT sponsor_accounts_status_check
    CHECK (status IN ('active', 'revoked'));

ALTER TABLE public.sponsor_accounts
    DROP CONSTRAINT IF EXISTS sponsor_accounts_role_check;
UPDATE public.sponsor_accounts
SET role = 'owner', updated_at = NOW()
WHERE role NOT IN ('owner', 'viewer');
ALTER TABLE public.sponsor_accounts
    ADD CONSTRAINT sponsor_accounts_role_check
    CHECK (role IN ('owner', 'viewer'));

WITH ranked_accounts AS (
    SELECT id,
           ROW_NUMBER() OVER (PARTITION BY user_id ORDER BY created_at, id) AS account_rank
    FROM public.sponsor_accounts
    WHERE status = 'active'
)
UPDATE public.sponsor_accounts AS account
SET status = 'revoked', updated_at = NOW()
FROM ranked_accounts
WHERE account.id = ranked_accounts.id
  AND ranked_accounts.account_rank > 1;

CREATE UNIQUE INDEX IF NOT EXISTS sponsor_accounts_one_active_partner_per_user_key
    ON public.sponsor_accounts (user_id)
    WHERE status = 'active';

ALTER TABLE public.partner_applications
    ADD COLUMN IF NOT EXISTS invitation_email TEXT,
    ADD COLUMN IF NOT EXISTS approved_sponsor_id UUID REFERENCES public.sponsors(id),
    ADD COLUMN IF NOT EXISTS approved_by UUID REFERENCES auth.users(id),
    ADD COLUMN IF NOT EXISTS approved_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS rejected_by UUID REFERENCES auth.users(id),
    ADD COLUMN IF NOT EXISTS rejected_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS rejection_reason TEXT;

CREATE INDEX IF NOT EXISTS partner_applications_status_created_idx
    ON public.partner_applications (status, created_at DESC);
CREATE INDEX IF NOT EXISTS partner_applications_contact_email_idx
    ON public.partner_applications (LOWER(contact_email));

ALTER TABLE public.partner_applications ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.partner_applications FORCE ROW LEVEL SECURITY;
REVOKE ALL ON public.partner_applications FROM anon, authenticated;
GRANT ALL ON public.partner_applications TO service_role;
