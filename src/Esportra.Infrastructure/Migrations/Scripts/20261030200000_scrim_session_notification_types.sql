-- PROJ-077 T2: New scrim session notification types.
-- ADD VALUE IF NOT EXISTS is idempotent; cannot run inside a transaction.
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_lobby_updated';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_checkin_updated';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_session_started';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_result_confirmed';
