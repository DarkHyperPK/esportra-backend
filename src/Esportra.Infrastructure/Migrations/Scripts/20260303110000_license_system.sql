-- License system: ESP-VO-XXXXXX / ESP-OR-XXXXXX / ESP-BC-XXXXXX identifiers
CREATE TABLE IF NOT EXISTS public.licenses (
  id UUID DEFAULT gen_random_uuid() PRIMARY KEY,
  user_id UUID NOT NULL REFERENCES public.profiles(id) ON DELETE CASCADE,
  license_id TEXT UNIQUE NOT NULL DEFAULT '',
  license_type TEXT NOT NULL
    CONSTRAINT licenses_type_check CHECK (license_type IN ('venue_owner','organizer','broadcaster')),
  status TEXT NOT NULL DEFAULT 'active'
    CONSTRAINT licenses_status_check CHECK (status IN ('active','suspended','revoked')),
  issued_at TIMESTAMPTZ DEFAULT NOW(),
  expires_at TIMESTAMPTZ,
  notes TEXT,
  created_at TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE public.licenses ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Users see own licenses"
  ON public.licenses FOR SELECT
  USING (user_id = auth.uid());

CREATE POLICY "Admins manage all licenses"
  ON public.licenses FOR ALL
  USING (
    EXISTS (
      SELECT 1 FROM public.profiles
      WHERE id = auth.uid() AND (is_admin = true OR 'venue_admin' = ANY(admin_roles))
    )
  );

-- Auto-generate license_id based on type prefix
CREATE OR REPLACE FUNCTION generate_license_id()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE
  prefix TEXT;
  candidate TEXT;
BEGIN
  IF NEW.license_id IS NULL OR NEW.license_id = '' THEN
    prefix := CASE NEW.license_type
      WHEN 'venue_owner'  THEN 'ESP-VO-'
      WHEN 'organizer'    THEN 'ESP-OR-'
      WHEN 'broadcaster'  THEN 'ESP-BC-'
      ELSE 'ESP-XX-'
    END;
    LOOP
      candidate := prefix || LPAD((FLOOR(RANDOM() * 900000) + 100000)::TEXT, 6, '0');
      EXIT WHEN NOT EXISTS (SELECT 1 FROM public.licenses WHERE license_id = candidate);
    END LOOP;
    NEW.license_id := candidate;
  END IF;
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS set_license_id ON public.licenses;
CREATE TRIGGER set_license_id
  BEFORE INSERT ON public.licenses
  FOR EACH ROW EXECUTE FUNCTION generate_license_id();
