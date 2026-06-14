-- Notification type for automatic check-in walkovers and organizer no-show alerts.
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'match_walkover';
