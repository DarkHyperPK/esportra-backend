-- Real-time live seating status pushed by Esportra Desktop Agent
CREATE TABLE IF NOT EXISTS public.venue_live_status (
  venue_id UUID PRIMARY KEY REFERENCES public.venues(id) ON DELETE CASCADE,
  seats_total INTEGER NOT NULL DEFAULT 0,
  seats_occupied INTEGER NOT NULL DEFAULT 0,
  is_open BOOLEAN NOT NULL DEFAULT false,
  updated_at TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE public.venue_live_status ENABLE ROW LEVEL SECURITY;

-- Public can read live status (shown on VenueDetails)
CREATE POLICY "Anyone can read live status"
  ON public.venue_live_status FOR SELECT
  USING (true);

-- Venue owners can upsert their own venue's live status
CREATE POLICY "Owners update own live status"
  ON public.venue_live_status FOR ALL
  USING (
    EXISTS (
      SELECT 1 FROM public.venues v
      WHERE v.id = venue_live_status.venue_id AND v.owner_id = auth.uid()
    )
  );

-- Enable Supabase Realtime on this table
ALTER TABLE public.venue_live_status REPLICA IDENTITY FULL;
