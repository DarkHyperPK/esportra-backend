-- Add link column to notifications (referenced by all new notification inserts
-- and the match_ready DB trigger, but never existed in the schema).

ALTER TABLE public.notifications
    ADD COLUMN IF NOT EXISTS link TEXT;

-- Extend notification_type enum with match-flow event types.
-- We add them individually to avoid conflicts if some already exist.

DO $$
BEGIN
    -- Match result flow
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'result_reported'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'result_reported';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'result_accepted'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'result_accepted';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'result_disputed'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'result_disputed';
    END IF;

    -- Dispute flow
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'dispute_filed'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'dispute_filed';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'dispute_resolved'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'dispute_resolved';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'dispute_rejected'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'dispute_rejected';
    END IF;

    -- Map veto flow
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'veto_your_turn'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'veto_your_turn';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'veto_completed'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'veto_completed';
    END IF;

    -- Match lifecycle
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'match_ready'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'match_ready';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'match_completed'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'match_completed';
    END IF;

    -- Tournament registration
    IF NOT EXISTS (SELECT 1 FROM pg_enum WHERE enumlabel = 'tournament_registered'
                   AND enumtypid = 'public.notification_type'::regtype) THEN
        ALTER TYPE public.notification_type ADD VALUE 'tournament_registered';
    END IF;
END;
$$;
