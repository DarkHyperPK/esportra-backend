-- ============================================================================
-- Migration: Competitive email notification support
-- Adds: notification_type enum values used by LFG/scrim email jobs
--       expiry_email_sent_at column on lfg_listings (idempotency guard for
--       the LfgListingExpiringJob hourly sweep)
-- ============================================================================

-- New notification_type values (IF NOT EXISTS is safe across replays)
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'lfg_listing_expiring';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'lfg_listing_expired';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_offer_expired';

-- Guard column for the LfgListingExpiringJob sweep — prevents double-send
ALTER TABLE public.lfg_listings
    ADD COLUMN IF NOT EXISTS expiry_email_sent_at TIMESTAMPTZ;
