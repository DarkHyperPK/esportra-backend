-- Notification types for Squad Up lobbies. ADD VALUE IF NOT EXISTS is idempotent.
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'squad_up_lobby_request';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'squad_up_lobby_accepted';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'squad_up_lobby_full';
