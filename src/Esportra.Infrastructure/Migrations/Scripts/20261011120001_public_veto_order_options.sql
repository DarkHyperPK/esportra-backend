-- Host-configured toss and map veto order, independent of OBS access.
ALTER TABLE public.public_veto_sessions
    ADD COLUMN IF NOT EXISTS toss_enabled boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS custom_sequence jsonb;
