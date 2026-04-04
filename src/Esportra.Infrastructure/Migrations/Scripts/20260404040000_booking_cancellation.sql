-- Booking cancellation and modification support
-- Migration: 20260404040000_booking_cancellation.sql

ALTER TABLE venue_bookings
  ADD COLUMN IF NOT EXISTS cancelled_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS cancelled_by UUID REFERENCES auth.users(id),
  ADD COLUMN IF NOT EXISTS cancellation_reason TEXT,
  ADD COLUMN IF NOT EXISTS refund_amount NUMERIC(10,2),
  ADD COLUMN IF NOT EXISTS original_booking_id UUID REFERENCES venue_bookings(id);

CREATE OR REPLACE FUNCTION cancel_booking(p_booking_id UUID, p_user_id UUID, p_reason TEXT DEFAULT NULL)
RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_booking venue_bookings%ROWTYPE;
BEGIN
  SELECT * INTO v_booking
  FROM venue_bookings
  WHERE id = p_booking_id
  FOR UPDATE;

  IF NOT FOUND THEN
    RETURN jsonb_build_object('success', false, 'message', 'Booking not found');
  END IF;

  IF v_booking.code_used_at IS NOT NULL THEN
    RETURN jsonb_build_object('success', false, 'message', 'Booking already used');
  END IF;

  IF v_booking.status = 'cancelled' THEN
    RETURN jsonb_build_object('success', false, 'message', 'Booking already cancelled');
  END IF;

  UPDATE venue_bookings
  SET status = 'cancelled',
      cancelled_at = NOW(),
      cancelled_by = p_user_id,
      cancellation_reason = p_reason,
      booking_code = NULL
  WHERE id = p_booking_id;

  RETURN jsonb_build_object('success', true, 'message', 'Booking cancelled');
END;
$$;
