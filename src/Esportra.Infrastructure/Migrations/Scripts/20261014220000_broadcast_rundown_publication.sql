-- Publication snapshots are planning metadata; existing ownership and RLS stay unchanged.
ALTER TABLE public.broadcast_shows
    ADD COLUMN IF NOT EXISTS rundown_version bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS published_rundown jsonb,
    ADD COLUMN IF NOT EXISTS published_configuration jsonb,
    ADD COLUMN IF NOT EXISTS rundown_published_at timestamptz;
