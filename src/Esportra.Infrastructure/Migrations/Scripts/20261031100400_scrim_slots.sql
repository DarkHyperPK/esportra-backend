-- PROJ-078 T4: Recurring slot calendar.
-- New scrim_slots table for bookable time slots on recurring listings.
-- UNIQUE(listing_id, starts_at) enforces non-overlap at DB level.
-- slot_id added to scrim_offers to link offers to specific slots.

-- ── scrim_slot_status enum ─────────────────────────────────────────────────────

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'scrim_slot_status') THEN
        CREATE TYPE public.scrim_slot_status AS ENUM ('available', 'booked', 'cancelled');
    END IF;
END $$;

-- ── scrim_slots table ──────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.scrim_slots (
    id                  UUID                NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
    listing_id          UUID                NOT NULL
        REFERENCES public.scrim_listings(id) ON DELETE CASCADE,
    starts_at           TIMESTAMPTZ         NOT NULL,
    ends_at             TIMESTAMPTZ         NOT NULL,
    status              scrim_slot_status   NOT NULL DEFAULT 'available',
    booked_by_team_id   UUID                NULL REFERENCES public.teams(id) ON DELETE SET NULL,
    session_id          UUID                NULL REFERENCES public.scrim_sessions(id) ON DELETE SET NULL,
    created_at          TIMESTAMPTZ         NOT NULL DEFAULT now()
);

-- Non-overlap: a listing can only have one slot per start time.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_scrim_slots_listing_starts') THEN
        ALTER TABLE public.scrim_slots
            ADD CONSTRAINT uq_scrim_slots_listing_starts
            UNIQUE (listing_id, starts_at);
    END IF;
END $$;

-- starts_at must be before ends_at
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_slot_time_order') THEN
        ALTER TABLE public.scrim_slots
            ADD CONSTRAINT chk_scrim_slot_time_order
            CHECK (ends_at > starts_at);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_scrim_slots_listing_available
    ON public.scrim_slots (listing_id, starts_at)
    WHERE status = 'available';

-- ── RLS ────────────────────────────────────────────────────────────────────────

ALTER TABLE public.scrim_slots ENABLE ROW LEVEL SECURITY;

-- Listing owner (captain/manager of the listing's team): full access
DROP POLICY IF EXISTS scrim_slots_listing_staff ON public.scrim_slots;
CREATE POLICY scrim_slots_listing_staff
    ON public.scrim_slots FOR ALL TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.scrim_listings sl
            JOIN public.team_members tm ON tm.team_id = sl.team_id
            WHERE sl.id = scrim_slots.listing_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    )
    WITH CHECK (
        EXISTS (
            SELECT 1 FROM public.scrim_listings sl
            JOIN public.team_members tm ON tm.team_id = sl.team_id
            WHERE sl.id = scrim_slots.listing_id
              AND tm.user_id = auth.uid()
              AND tm.role IN ('captain', 'manager')
              AND tm.is_active = TRUE
        )
    );

-- Authenticated users: read available slots on open listings
DROP POLICY IF EXISTS scrim_slots_public_read ON public.scrim_slots;
CREATE POLICY scrim_slots_public_read
    ON public.scrim_slots FOR SELECT TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.scrim_listings sl
            WHERE sl.id = scrim_slots.listing_id
              AND sl.status = 'open'
        )
    );

-- Service role: full access
DROP POLICY IF EXISTS scrim_slots_service ON public.scrim_slots;
CREATE POLICY scrim_slots_service
    ON public.scrim_slots FOR ALL TO service_role
    USING (true) WITH CHECK (true);

-- ── slot_id on scrim_offers ────────────────────────────────────────────────────

ALTER TABLE public.scrim_offers
    ADD COLUMN IF NOT EXISTS slot_id UUID NULL
        REFERENCES public.scrim_slots(id) ON DELETE SET NULL;
