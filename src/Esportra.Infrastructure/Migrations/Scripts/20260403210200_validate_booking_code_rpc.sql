-- Phase 2A.3: Booking code validation RPC
-- Called by SignalR hub (service role) when station agent submits a booking code

CREATE OR REPLACE FUNCTION validate_booking_code(
  p_code TEXT,
  p_station_id TEXT,
  p_venue_id UUID
)
RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_booking venue_bookings%ROWTYPE;
  v_now TIMESTAMPTZ := NOW();
  v_starts_at TIMESTAMPTZ;
  v_ends_at TIMESTAMPTZ;
BEGIN
  -- Only service role (hub) can call this RPC
  IF current_setting('request.jwt.claim.role', true) != 'service_role' THEN
    RAISE EXCEPTION 'Unauthorized: only service role can validate booking codes';
  END IF;

  IF p_code IS NULL OR TRIM(p_code) = '' THEN
    RETURN jsonb_build_object('valid', false, 'message', 'Booking code is required');
  END IF;

  SELECT * INTO v_booking
  FROM venue_bookings
  WHERE booking_code = UPPER(TRIM(p_code))
    AND venue_id = p_venue_id
    AND code_used_at IS NULL
    AND status = 'confirmed'
  FOR UPDATE SKIP LOCKED;

  IF NOT FOUND THEN
    RETURN jsonb_build_object('valid', false, 'message', 'Invalid or expired booking code');
  END IF;

  -- Build timestamps from date + time (compared as naive, same as NOW() in DB timezone)
  v_starts_at := v_booking.booking_date + v_booking.start_time;
  v_ends_at   := v_booking.booking_date + v_booking.end_time;

  -- Allow entry 15 minutes early
  IF v_now < v_starts_at - INTERVAL '15 minutes' THEN
    RETURN jsonb_build_object(
      'valid', false,
      'message', 'Booking not yet active. Starts at ' || v_booking.start_time::TEXT
    );
  END IF;

  IF v_now > v_ends_at THEN
    RETURN jsonb_build_object('valid', false, 'message', 'Booking has expired');
  END IF;

  -- Check station preference
  IF v_booking.station_preference IS NOT NULL
    AND v_booking.station_preference != p_station_id THEN
    RETURN jsonb_build_object(
      'valid', false,
      'message', 'This code is for station ' || v_booking.station_preference
    );
  END IF;

  -- Consume the code
  UPDATE venue_bookings
  SET code_used_at = v_now,
      station_id   = p_station_id,
      status       = 'completed'
  WHERE id = v_booking.id;

  RETURN jsonb_build_object(
    'valid',            true,
    'message',          'Booking confirmed',
    'booking_id',       v_booking.id,
    'user_id',          v_booking.user_id,
    'duration_minutes', GREATEST(EXTRACT(EPOCH FROM (v_ends_at - v_now)) / 60, 1),
    'display_name',     COALESCE(v_booking.contact_email, 'Customer'),
    'session_type',     'web_booking'
  );
END;
$$;
