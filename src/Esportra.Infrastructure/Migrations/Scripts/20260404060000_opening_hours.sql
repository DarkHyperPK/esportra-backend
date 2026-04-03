-- Opening hours JSONB column on venues + helper RPC
-- Migration: 20260404060000_opening_hours.sql

ALTER TABLE venues
  ADD COLUMN IF NOT EXISTS opening_hours JSONB DEFAULT '{}';

COMMENT ON COLUMN venues.opening_hours IS 'JSON: { mon: {open:"09:00",close:"23:00"}, ..., exceptions: [{date:"2026-12-25",closed:true}] }';

CREATE OR REPLACE FUNCTION is_venue_open(p_venue_id UUID, p_at TIMESTAMPTZ DEFAULT NOW())
RETURNS BOOLEAN
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
  v_hours JSONB;
  v_day TEXT;
  v_open TIME;
  v_close TIME;
BEGIN
  SELECT opening_hours INTO v_hours FROM venues WHERE id = p_venue_id;
  IF v_hours IS NULL OR v_hours = '{}'::jsonb THEN RETURN TRUE; END IF;
  v_day := LOWER(TO_CHAR(p_at AT TIME ZONE 'UTC', 'Dy'));
  IF NOT v_hours ? v_day THEN RETURN FALSE; END IF;
  v_open  := (v_hours -> v_day ->> 'open')::TIME;
  v_close := (v_hours -> v_day ->> 'close')::TIME;
  RETURN (p_at AT TIME ZONE 'UTC')::TIME BETWEEN v_open AND v_close;
END;
$$;
