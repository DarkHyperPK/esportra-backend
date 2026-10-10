-- Add last_synced_at to broadcast_shows.
-- Stamped by PushShowConfig when a ShowConfig payload is pushed to the org's node group.
-- NULL means the show has never been synced to a node.

ALTER TABLE public.broadcast_shows
    ADD COLUMN IF NOT EXISTS last_synced_at TIMESTAMPTZ;
