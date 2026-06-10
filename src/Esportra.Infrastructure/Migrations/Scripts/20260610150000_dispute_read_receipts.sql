-- Per-user read tracking for organizer dispute inbox (tab badges, unread comments)

CREATE TABLE IF NOT EXISTS public.dispute_read_receipts (
    dispute_id   uuid        NOT NULL REFERENCES public.tournament_disputes(id) ON DELETE CASCADE,
    user_id      uuid        NOT NULL REFERENCES public.profiles(id) ON DELETE CASCADE,
    last_read_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (dispute_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_dispute_read_receipts_user_id
    ON public.dispute_read_receipts (user_id);

GRANT SELECT, INSERT, UPDATE, DELETE ON public.dispute_read_receipts TO service_role;
