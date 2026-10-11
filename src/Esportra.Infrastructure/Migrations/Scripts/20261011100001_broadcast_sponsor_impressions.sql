-- ============================================================================
-- T-DB-1: broadcast_sponsor_impressions
-- PROJ-059 P4 (Esportra Air: Results + Reports)
--
-- Table created (idempotent):
--   public.broadcast_sponsor_impressions — append-only record of when a sponsor
--     overlay was displayed during a broadcast show.
--
-- Ownership model: all writes go through service_role (the Desktop node and the
-- backend API); authenticated org owners may only SELECT. This matches the
-- broadcast_events pattern exactly.
--
-- ended_at is nullable: the Desktop node writes started_at when the overlay
-- appears and a later call (or reconnect/show-end cleanup) writes ended_at.
-- If the Desktop crashes the row stays open; the API detects open impressions
-- via (ended_at IS NULL) and closes them on reconnect or show end.
--
-- segment_id is nullable: an impression may fire outside a named segment
-- (e.g. ad-hoc, between segments, or when the rundown has no active segment).
-- ============================================================================

CREATE TABLE IF NOT EXISTS public.broadcast_sponsor_impressions (
    id         UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    show_id    UUID        NOT NULL REFERENCES public.broadcast_shows(id) ON DELETE CASCADE,
    sponsor_id UUID        NOT NULL REFERENCES public.broadcast_show_sponsors(id) ON DELETE CASCADE,
    segment_id UUID        REFERENCES public.rundown_segments(id) ON DELETE SET NULL,  -- nullable; see header
    started_at TIMESTAMPTZ NOT NULL,
    ended_at   TIMESTAMPTZ,                                                             -- nullable; open impression if Desktop crashes
    slot_type  TEXT        NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- (show_id) — aggregation queries (total impressions for a show)
CREATE INDEX IF NOT EXISTS broadcast_sponsor_impressions_show_id_idx
    ON public.broadcast_sponsor_impressions (show_id);

-- (show_id, sponsor_id) — per-sponsor aggregation within a show
CREATE INDEX IF NOT EXISTS broadcast_sponsor_impressions_show_sponsor_idx
    ON public.broadcast_sponsor_impressions (show_id, sponsor_id);

-- (show_id, ended_at) — open-impression detection: WHERE ended_at IS NULL
CREATE INDEX IF NOT EXISTS broadcast_sponsor_impressions_show_ended_idx
    ON public.broadcast_sponsor_impressions (show_id, ended_at);

ALTER TABLE public.broadcast_sponsor_impressions ENABLE ROW LEVEL SECURITY;

-- SELECT: authenticated org owner of the show
-- Chain: broadcast_sponsor_impressions.show_id → broadcast_shows → organizations.owner_id
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies
    WHERE tablename = 'broadcast_sponsor_impressions'
      AND policyname = 'broadcast_sponsor_impressions_org_select'
  ) THEN
    CREATE POLICY "broadcast_sponsor_impressions_org_select"
      ON public.broadcast_sponsor_impressions
      FOR SELECT TO authenticated
      USING (
        EXISTS (
          SELECT 1 FROM public.broadcast_shows bs
          JOIN public.organizations o ON o.id = bs.org_id
          WHERE bs.id = broadcast_sponsor_impressions.show_id
            AND o.owner_id = auth.uid()
        )
      );
  END IF;
END $$;

-- No INSERT / UPDATE / DELETE policies for authenticated.
-- All writes (including setting ended_at) are performed by the backend API
-- via service_role, which bypasses RLS.

GRANT SELECT ON public.broadcast_sponsor_impressions TO authenticated;
GRANT ALL ON public.broadcast_sponsor_impressions TO service_role;
