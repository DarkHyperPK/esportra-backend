-- Notification type for Queue Now (LFG swipe) matches. ADD VALUE IF NOT EXISTS is idempotent.
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'lfg_swipe_match';
