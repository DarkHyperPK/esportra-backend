-- Dedicated read-only token for public veto OBS browser sources.
ALTER TABLE public.public_veto_sessions
    ADD COLUMN IF NOT EXISTS overlay_token text;

-- PRE-FLIGHT: Run on production before deploying and confirm the number of active sessions needing a viewer token:
-- SELECT count(*) FROM public.public_veto_sessions WHERE overlay_token IS NULL;
-- Expected: existing sessions without a dedicated overlay token; each receives one unique random token.
UPDATE public.public_veto_sessions
SET overlay_token = encode(extensions.gen_random_bytes(32), 'hex')
WHERE overlay_token IS NULL;

ALTER TABLE public.public_veto_sessions
    ALTER COLUMN overlay_token SET DEFAULT encode(extensions.gen_random_bytes(32), 'hex'),
    ALTER COLUMN overlay_token SET NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_public_veto_sessions_overlay_token
    ON public.public_veto_sessions (overlay_token);

ALTER TABLE public.public_veto_sessions ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.public_veto_actions ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE schemaname = 'public' AND tablename = 'public_veto_sessions' AND policyname = 'public_veto_sessions_service_all') THEN
        CREATE POLICY public_veto_sessions_service_all ON public.public_veto_sessions
            FOR ALL TO service_role USING (true) WITH CHECK (true);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE schemaname = 'public' AND tablename = 'public_veto_actions' AND policyname = 'public_veto_actions_service_all') THEN
        CREATE POLICY public_veto_actions_service_all ON public.public_veto_actions
            FOR ALL TO service_role USING (true) WITH CHECK (true);
    END IF;
END $$;
