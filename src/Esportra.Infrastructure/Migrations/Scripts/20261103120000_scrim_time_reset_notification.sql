-- Notification for a scrim whose agreed time was cleared. ADD VALUE IF NOT EXISTS is idempotent.
-- Cannot run inside a transaction.
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_time_reset';
