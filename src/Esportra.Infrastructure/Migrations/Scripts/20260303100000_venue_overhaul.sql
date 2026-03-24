-- ============================================================
-- Venue Management Overhaul
-- Adds: venue_id (VEN-XXXXXX), status workflow, price_per_hour,
--       subscription_tier, desktop_pairing_token
-- ============================================================

-- 1. Add new columns to venues
ALTER TABLE public.venues
  ADD COLUMN IF NOT EXISTS venue_id TEXT UNIQUE,
  ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'draft'
    CONSTRAINT venues_status_check CHECK (
      status IN ('draft','pending_review','published','rejected','suspended','archived')
    ),
  ADD COLUMN IF NOT EXISTS rejection_reason TEXT,
  ADD COLUMN IF NOT EXISTS reviewed_by UUID REFERENCES public.profiles(id),
  ADD COLUMN IF NOT EXISTS reviewed_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS submitted_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS published_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS subscription_tier TEXT NOT NULL DEFAULT 'free'
    CONSTRAINT venues_tier_check CHECK (
      subscription_tier IN ('free','basic','pro','enterprise')
    ),
  ADD COLUMN IF NOT EXISTS desktop_pairing_token TEXT UNIQUE,
  ADD COLUMN IF NOT EXISTS price_per_hour NUMERIC(10,2) DEFAULT 0;

-- 2. Auto-generate VEN-XXXXXX on INSERT (6-digit zero-padded random)
CREATE OR REPLACE FUNCTION generate_venue_id()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE
  candidate TEXT;
BEGIN
  IF NEW.venue_id IS NULL THEN
    LOOP
      candidate := 'VEN-' || LPAD((FLOOR(RANDOM() * 900000) + 100000)::TEXT, 6, '0');
      EXIT WHEN NOT EXISTS (SELECT 1 FROM public.venues WHERE venue_id = candidate);
    END LOOP;
    NEW.venue_id := candidate;
  END IF;
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS set_venue_id ON public.venues;
CREATE TRIGGER set_venue_id
  BEFORE INSERT ON public.venues
  FOR EACH ROW EXECUTE FUNCTION generate_venue_id();

-- 3. Auto-generate 6-char alphanumeric pairing token on INSERT
--    Excludes ambiguous chars (0, O, I, 1)
CREATE OR REPLACE FUNCTION generate_pairing_token()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE
  chars TEXT := 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  candidate TEXT;
  i INTEGER;
BEGIN
  IF NEW.desktop_pairing_token IS NULL THEN
    LOOP
      candidate := '';
      FOR i IN 1..6 LOOP
        candidate := candidate || SUBSTR(chars, FLOOR(RANDOM() * LENGTH(chars) + 1)::INT, 1);
      END LOOP;
      EXIT WHEN NOT EXISTS (SELECT 1 FROM public.venues WHERE desktop_pairing_token = candidate);
    END LOOP;
    NEW.desktop_pairing_token := candidate;
  END IF;
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS set_pairing_token ON public.venues;
CREATE TRIGGER set_pairing_token
  BEFORE INSERT ON public.venues
  FOR EACH ROW EXECUTE FUNCTION generate_pairing_token();

-- 4. Backfill existing venues: give them IDs, tokens, mark as published
DO $$
DECLARE
  v RECORD;
  vid TEXT;
  ptok TEXT;
  chars TEXT := 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  i INT;
BEGIN
  FOR v IN SELECT id FROM public.venues WHERE venue_id IS NULL LOOP
    -- Generate unique venue_id
    LOOP
      vid := 'VEN-' || LPAD((FLOOR(RANDOM() * 900000) + 100000)::TEXT, 6, '0');
      EXIT WHEN NOT EXISTS (SELECT 1 FROM public.venues WHERE venue_id = vid);
    END LOOP;
    -- Generate unique pairing token
    LOOP
      ptok := '';
      FOR i IN 1..6 LOOP
        ptok := ptok || SUBSTR(chars, FLOOR(RANDOM() * LENGTH(chars) + 1)::INT, 1);
      END LOOP;
      EXIT WHEN NOT EXISTS (SELECT 1 FROM public.venues WHERE desktop_pairing_token = ptok);
    END LOOP;

    UPDATE public.venues
    SET
      venue_id = vid,
      desktop_pairing_token = ptok,
      status = CASE WHEN is_active = true THEN 'published' ELSE 'draft' END,
      published_at = CASE WHEN is_active = true THEN created_at ELSE NULL END
    WHERE id = v.id;
  END LOOP;
END;
$$;

-- 5. RLS: update SELECT policy so public only sees published venues
--    Owners see their own (any status). Admins see all.
ALTER TABLE public.venues ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS "Published venues are viewable by everyone" ON public.venues;
CREATE POLICY "Published venues are viewable by everyone"
  ON public.venues FOR SELECT
  USING (
    status = 'published'
    OR (auth.uid() IS NOT NULL AND owner_id = auth.uid())
    OR EXISTS (
      SELECT 1 FROM public.profiles
      WHERE id = auth.uid()
        AND (is_admin = true OR 'venue_admin' = ANY(admin_roles::text[]))
    )
  );

-- 6. Allow admins to update status, reviewed_by, rejection_reason etc.
DROP POLICY IF EXISTS "Admins can update any venue" ON public.venues;
CREATE POLICY "Admins can update any venue"
  ON public.venues FOR UPDATE
  USING (
    EXISTS (
      SELECT 1 FROM public.profiles
      WHERE id = auth.uid()
        AND (is_admin = true OR 'venue_admin' = ANY(admin_roles::text[]))
    )
  );

-- Allow venue owners to update their own venues (but not status to 'published' directly)
DROP POLICY IF EXISTS "Owners can update their own venues" ON public.venues;
CREATE POLICY "Owners can update their own venues"
  ON public.venues FOR UPDATE
  USING (auth.uid() IS NOT NULL AND owner_id = auth.uid())
  WITH CHECK (
    -- Owners cannot self-approve; they can only set draft/pending_review
    status IN ('draft', 'pending_review')
  );
