-- Persist walkover flag directly on brkt_matches so the organizer-awarded
-- walkover label survives a page refresh instead of being inferred from checkin state.

ALTER TABLE public.brkt_matches
    ADD COLUMN IF NOT EXISTS is_walkover BOOLEAN NOT NULL DEFAULT FALSE;
