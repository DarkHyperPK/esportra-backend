CREATE TABLE IF NOT EXISTS public.account_security_state (
    user_id UUID PRIMARY KEY REFERENCES auth.users(id) ON DELETE CASCADE,
    sessions_valid_after BIGINT NOT NULL DEFAULT 0,
    revocation_version BIGINT NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CHECK (sessions_valid_after >= 0),
    CHECK (revocation_version >= 0)
);

ALTER TABLE public.account_security_state ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS account_security_state_service_role_all ON public.account_security_state;
CREATE POLICY account_security_state_service_role_all
    ON public.account_security_state
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);

GRANT ALL ON public.account_security_state TO service_role;