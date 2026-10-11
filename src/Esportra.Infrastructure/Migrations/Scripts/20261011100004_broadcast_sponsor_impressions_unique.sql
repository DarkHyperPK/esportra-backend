-- ============================================================================
-- PROJ-059 P4 — CIO L2: idempotency on broadcast_sponsor_impressions INSERT
--
-- Adds a unique constraint on (show_id, sponsor_id, started_at) so that a
-- desktop node retry or drain replay cannot create duplicate rows. The
-- BroadcastSponsorProofEndpoints INSERT uses ON CONFLICT DO NOTHING.
--
-- The combination is fine as a uniqueness key: the same sponsor cannot begin
-- playing at the same millisecond twice in the same show.
-- ============================================================================

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'broadcast_sponsor_impressions_show_sponsor_started_key'
  ) THEN
    ALTER TABLE public.broadcast_sponsor_impressions
      ADD CONSTRAINT broadcast_sponsor_impressions_show_sponsor_started_key
        UNIQUE (show_id, sponsor_id, started_at);
  END IF;
END $$;
