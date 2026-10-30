-- PROJ-078 T3: Five new scrim notification_type enum values.
-- ADD VALUE IF NOT EXISTS is idempotent. Cannot run inside a transaction.
-- These types support check-in window automation, timeout cancellation,
-- veto readiness, and dispute resolution notifications.

ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_checkin_window_open';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_reminder_30min';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_timeout_cancelled';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_veto_ready';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_dispute_resolved';
