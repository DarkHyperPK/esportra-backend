-- Notification type for organizer-driven match schedule changes.

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_enum
        WHERE enumlabel = 'match_schedule_changed'
          AND enumtypid = 'public.notification_type'::regtype
    ) THEN
        ALTER TYPE public.notification_type ADD VALUE 'match_schedule_changed';
    END IF;
END;
$$;
