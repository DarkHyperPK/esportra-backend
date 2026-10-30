-- PROJ-077 M4: Recurring listing fields
-- Extends scrim_listings with schedule_type, schedule_until, slot_duration_minutes.
-- Existing rows default to 'one_off' which is the correct semantic backfill.
-- New columns inherit existing row-level RLS coverage automatically.

ALTER TABLE public.scrim_listings
    ADD COLUMN IF NOT EXISTS schedule_type         TEXT        NOT NULL DEFAULT 'one_off',
    ADD COLUMN IF NOT EXISTS schedule_until        TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS slot_duration_minutes INT         NULL;

-- schedule_type must be one of the two recognised values
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_listing_schedule_type') THEN
        ALTER TABLE public.scrim_listings
            ADD CONSTRAINT chk_scrim_listing_schedule_type
            CHECK (schedule_type IN ('one_off', 'recurring'));
    END IF;
END $$;

-- schedule_until is only meaningful on recurring listings
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_listing_schedule_until') THEN
        ALTER TABLE public.scrim_listings
            ADD CONSTRAINT chk_scrim_listing_schedule_until
            CHECK (schedule_until IS NULL OR schedule_type = 'recurring');
    END IF;
END $$;

-- slot_duration_minutes is only meaningful on recurring listings and must be positive
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_listing_slot_duration') THEN
        ALTER TABLE public.scrim_listings
            ADD CONSTRAINT chk_scrim_listing_slot_duration
            CHECK (
                slot_duration_minutes IS NULL
                OR (schedule_type = 'recurring' AND slot_duration_minutes > 0)
            );
    END IF;
END $$;
