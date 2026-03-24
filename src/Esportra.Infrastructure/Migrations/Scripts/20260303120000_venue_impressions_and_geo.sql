-- Venue impressions tracking (fire-and-forget analytics)
CREATE TABLE IF NOT EXISTS public.venue_impressions (
  id UUID DEFAULT gen_random_uuid() PRIMARY KEY,
  venue_id UUID NOT NULL REFERENCES public.venues(id) ON DELETE CASCADE,
  event_type TEXT NOT NULL
    CONSTRAINT impressions_type_check CHECK (
      event_type IN ('view','card_view','booking_click','contact_click')
    ),
  user_id UUID REFERENCES public.profiles(id) ON DELETE SET NULL,
  session_id TEXT,
  created_at TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE public.venue_impressions ENABLE ROW LEVEL SECURITY;

-- Anyone can insert impressions (fire-and-forget tracking)
CREATE POLICY "Anyone can log impressions"
  ON public.venue_impressions FOR INSERT
  WITH CHECK (true);

-- Venue owners can read their own venue impressions
-- Use alias + explicit table prefix to avoid ambiguity with venues.venue_id (TEXT)
CREATE POLICY "Owners see own venue impressions"
  ON public.venue_impressions FOR SELECT
  USING (
    EXISTS (
      SELECT 1 FROM public.venues v
      WHERE v.id = venue_impressions.venue_id AND v.owner_id = auth.uid()
    )
  );

-- Index for fast per-venue queries
CREATE INDEX IF NOT EXISTS idx_venue_impressions_venue_id
  ON public.venue_impressions (venue_id, event_type, created_at DESC);

-- Geo search using Haversine formula — no PostGIS required
CREATE OR REPLACE FUNCTION find_nearby_venues(
  user_lat FLOAT,
  user_lng  FLOAT,
  radius_km FLOAT DEFAULT 50
)
RETURNS SETOF public.venues
LANGUAGE sql STABLE AS $$
  SELECT v.*
  FROM public.venues v
  WHERE
    v.status = 'published'
    AND v.latitude  IS NOT NULL
    AND v.longitude IS NOT NULL
    AND (
      6371 * acos(
        LEAST(1.0,
          cos(radians(user_lat)) * cos(radians(v.latitude)) *
          cos(radians(v.longitude) - radians(user_lng)) +
          sin(radians(user_lat)) * sin(radians(v.latitude))
        )
      )
    ) <= radius_km
  ORDER BY (
      6371 * acos(
        LEAST(1.0,
          cos(radians(user_lat)) * cos(radians(v.latitude)) *
          cos(radians(v.longitude) - radians(user_lng)) +
          sin(radians(user_lat)) * sin(radians(v.latitude))
        )
      )
    ) ASC;
$$;
