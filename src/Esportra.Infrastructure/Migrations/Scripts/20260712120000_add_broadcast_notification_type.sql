-- Add 'broadcast' to the notification_type enum so BroadcastSendJob
-- can insert into the notifications table with type = 'broadcast'
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'broadcast';
