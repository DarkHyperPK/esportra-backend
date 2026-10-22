-- Withdrawal request + refund proof tracking for paid tournament participants.
-- Participants request withdrawal; organizer processes refund outside platform
-- and uploads proof. Esportra keeps the record.

-- Add withdrawal_pending to the registration_status enum
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_enum
    WHERE enumlabel = 'withdrawal_pending'
      AND enumtypid = 'public.registration_status'::regtype
  ) THEN
    ALTER TYPE public.registration_status ADD VALUE 'withdrawal_pending';
  END IF;
END $$;

ALTER TABLE public.tournament_participants
  ADD COLUMN IF NOT EXISTS withdrawal_requested_at timestamptz,
  ADD COLUMN IF NOT EXISTS withdrawal_reason text,
  ADD COLUMN IF NOT EXISTS refund_proof_url text,
  ADD COLUMN IF NOT EXISTS refund_processed_at timestamptz,
  ADD COLUMN IF NOT EXISTS refund_processed_by uuid REFERENCES auth.users(id),
  ADD COLUMN IF NOT EXISTS refund_notes text;
