-- Show-specific presentation settings; existing organization authorization and RLS remain unchanged.
ALTER TABLE public.broadcast_shows
    ADD COLUMN IF NOT EXISTS appearance jsonb NOT NULL DEFAULT '{}'::jsonb;
