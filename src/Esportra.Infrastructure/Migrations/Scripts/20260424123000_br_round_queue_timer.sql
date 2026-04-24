-- Add configurable queue timer support for BR rounds.
-- queue_started_at is stamped when the lobby code becomes live for an active round.

ALTER TABLE public.br_rounds
    ADD COLUMN IF NOT EXISTS queue_timer_minutes INT,
    ADD COLUMN IF NOT EXISTS queue_started_at TIMESTAMPTZ;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'br_rounds_queue_timer_minutes_check'
    ) THEN
        ALTER TABLE public.br_rounds
            ADD CONSTRAINT br_rounds_queue_timer_minutes_check
            CHECK (
                queue_timer_minutes IS NULL
                OR (queue_timer_minutes >= 0 AND queue_timer_minutes <= 180)
            );
    END IF;
END $$;
