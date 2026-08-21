-- Adds payout method selection to tournaments.
-- Organizers choose between 'manual' (describe their own process) or 'gateway'
-- (future automated payout via payment gateway — behaves like manual until integrated).

ALTER TABLE public.tournaments
    ADD COLUMN IF NOT EXISTS payout_method TEXT NOT NULL DEFAULT 'manual'
        CHECK (payout_method IN ('gateway', 'manual')),
    ADD COLUMN IF NOT EXISTS manual_payout_notes TEXT;
