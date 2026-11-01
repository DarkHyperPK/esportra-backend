-- PROJ-078 F1: Best-of series format for scrims (BO1/BO3/BO5).
-- scrim_listings.best_of      — the match format the listing offers (fixed by listing).
-- scrim_sessions.best_of      — copied from listing on offer acceptance; drives result validation.
-- Existing sessions default to BO1, which matches the previous single-score behaviour.
-- New columns inherit existing table-level row security automatically.

ALTER TABLE public.scrim_listings
    ADD COLUMN IF NOT EXISTS best_of INT NOT NULL DEFAULT 1;

ALTER TABLE public.scrim_sessions
    ADD COLUMN IF NOT EXISTS best_of INT NOT NULL DEFAULT 1;

-- Only recognised series formats
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_listing_best_of') THEN
        ALTER TABLE public.scrim_listings
            ADD CONSTRAINT chk_scrim_listing_best_of
            CHECK (best_of IN (1, 3, 5));
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_session_best_of') THEN
        ALTER TABLE public.scrim_sessions
            ADD CONSTRAINT chk_scrim_session_best_of
            CHECK (best_of IN (1, 3, 5));
    END IF;
END $$;
