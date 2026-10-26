-- LFG interest lifecycle and scrim offer lifecycle notification types.
-- ADD VALUE IF NOT EXISTS is idempotent; cannot run inside a transaction.
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'lfg_interest_received';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'lfg_interest_accepted';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'lfg_interest_declined';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_offer_received';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_offer_accepted';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_offer_declined';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_offer_withdrawn';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_result_submitted';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_dispute_raised';
