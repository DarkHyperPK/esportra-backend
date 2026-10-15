-- Opt-in stage following. Existing show/match RLS remains unchanged.
ALTER TABLE public.broadcast_shows
    ADD COLUMN IF NOT EXISTS follow_bracket boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS followed_stage_ids uuid[] NOT NULL DEFAULT '{}',
    ADD COLUMN IF NOT EXISTS enrollment_revision integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS enrollment_checked_at timestamptz,
    ADD COLUMN IF NOT EXISTS enrollment_error text;
CREATE INDEX IF NOT EXISTS broadcast_shows_follow_bracket_idx
    ON public.broadcast_shows (enrollment_checked_at)
    WHERE follow_bracket AND status IN ('draft', 'scheduled', 'live');
