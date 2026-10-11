-- Track only observed overlay state loads; existing sessions remain unknown until loaded.
-- Additive and idempotent. Existing RLS policies and grants are unchanged.
ALTER TABLE public.public_veto_sessions
    ADD COLUMN IF NOT EXISTS overlay_first_loaded_at timestamptz;
