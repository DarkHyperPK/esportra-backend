-- Tell the opposing team when a scrim is cancelled by the other side.
-- ADD VALUE IF NOT EXISTS is idempotent. Cannot run inside a transaction.
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scrim_session_cancelled';
