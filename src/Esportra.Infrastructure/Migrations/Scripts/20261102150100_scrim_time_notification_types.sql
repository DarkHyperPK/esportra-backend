-- Notification types for scrim scheduling. ADD VALUE IF NOT EXISTS is idempotent.
-- Cannot run inside a transaction.
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_time_proposed';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_time_accepted';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_time_declined';
