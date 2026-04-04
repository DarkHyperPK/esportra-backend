-- Phase 2A.1: Add booking code columns to venue_bookings
-- Enables lock-screen booking code validation flow

ALTER TABLE venue_bookings
  ADD COLUMN IF NOT EXISTS booking_code TEXT UNIQUE,
  ADD COLUMN IF NOT EXISTS code_used_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS station_preference TEXT,
  ADD COLUMN IF NOT EXISTS station_id TEXT;

-- Fast lookup for unused booking codes (lock screen validation)
CREATE INDEX IF NOT EXISTS idx_venue_bookings_booking_code
  ON venue_bookings(booking_code)
  WHERE code_used_at IS NULL;

COMMENT ON COLUMN venue_bookings.booking_code IS 'Alphanumeric code shown to customer (e.g. HJK3P9A2)';
COMMENT ON COLUMN venue_bookings.code_used_at IS 'Timestamp when code was consumed at a station';
COMMENT ON COLUMN venue_bookings.station_preference IS 'Optional preferred station ID (text, e.g. station-pc-01)';
COMMENT ON COLUMN venue_bookings.station_id IS 'Actual station where code was redeemed';

-- Normalize booking codes to uppercase on write
CREATE OR REPLACE FUNCTION normalize_booking_code()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.booking_code IS NOT NULL THEN
    NEW.booking_code := UPPER(TRIM(NEW.booking_code));
  END IF;
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_normalize_booking_code ON venue_bookings;
CREATE TRIGGER trg_normalize_booking_code
  BEFORE INSERT OR UPDATE ON venue_bookings
  FOR EACH ROW EXECUTE FUNCTION normalize_booking_code();
