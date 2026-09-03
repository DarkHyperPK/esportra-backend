-- Preserve tournament attribution in analytics tables when a tournament is deleted.
-- Raw events and content stats use SET NULL on the FK and store tournament_name at
-- write time so the attribution survives deletion. Slot daily stats use RESTRICT
-- because tournament_id is part of the PK and rows cannot be meaningfully retained
-- without it — blocking deletion protects the data instead.

-- sponsor_analytics_events: add tournament_name snapshot
ALTER TABLE public.sponsor_analytics_events
    ADD COLUMN IF NOT EXISTS tournament_name TEXT;

-- The immutability trigger (sponsor_analytics_events_no_update) blocks all updates
-- that are not a cascade-NULLing of tournament_id. Disable it for the backfill only.
ALTER TABLE public.sponsor_analytics_events DISABLE TRIGGER sponsor_analytics_events_no_update;

UPDATE public.sponsor_analytics_events e
SET tournament_name = t.name
FROM public.tournaments t
WHERE e.tournament_id = t.id
  AND e.tournament_name IS NULL;

ALTER TABLE public.sponsor_analytics_events ENABLE TRIGGER sponsor_analytics_events_no_update;

-- sponsor_content_daily_stats: add tournament_name snapshot
ALTER TABLE public.sponsor_content_daily_stats
    ADD COLUMN IF NOT EXISTS tournament_name TEXT;

UPDATE public.sponsor_content_daily_stats c
SET tournament_name = t.name
FROM public.tournaments t
WHERE c.tournament_id = t.id
  AND c.tournament_name IS NULL;

-- sponsor_slot_daily_stats: change FK from CASCADE to RESTRICT (data cannot survive
-- without tournament_id in PK; block deletion rather than silently destroy analytics)
ALTER TABLE public.sponsor_slot_daily_stats
    DROP CONSTRAINT IF EXISTS sponsor_slot_daily_stats_tournament_id_fkey;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'sponsor_slot_daily_stats_tournament_id_fkey'
    ) THEN
        ALTER TABLE public.sponsor_slot_daily_stats
            ADD CONSTRAINT sponsor_slot_daily_stats_tournament_id_fkey
            FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE RESTRICT;
    END IF;
END $$;

ALTER TABLE public.sponsor_slot_daily_stats
    ADD COLUMN IF NOT EXISTS tournament_name TEXT;

UPDATE public.sponsor_slot_daily_stats s
SET tournament_name = t.name
FROM public.tournaments t
WHERE s.tournament_id = t.id
  AND s.tournament_name IS NULL;

-- Re-declare immutability trigger now that tournament_name exists on the table.
-- Migration 000000 installed a version that did not yet know about this column;
-- a simultaneous SET NULL + tournament_name overwrite would have passed that check.
CREATE OR REPLACE FUNCTION public.reject_sponsor_analytics_event_update()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.tournament_id IS NOT NULL
       AND NEW.tournament_id IS NULL
       AND NEW.tournament_name IS NOT DISTINCT FROM OLD.tournament_name
       AND NEW.event_id = OLD.event_id
       AND NEW.sponsor_id = OLD.sponsor_id
       AND NEW.audience_id = OLD.audience_id
       AND NEW.identity_kind = OLD.identity_kind
       AND NEW.event_type = OLD.event_type
       AND NEW.placement = OLD.placement
       AND NEW.page_path IS NOT DISTINCT FROM OLD.page_path
       AND NEW.country_code IS NOT DISTINCT FROM OLD.country_code
       AND NEW.country_provenance = OLD.country_provenance
       AND NEW.age_band IS NOT DISTINCT FROM OLD.age_band
       AND NEW.age_provenance = OLD.age_provenance
       AND NEW.schema_version = OLD.schema_version
       AND NEW.received_at = OLD.received_at
       AND NEW.event_date_utc = OLD.event_date_utc
    THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'sponsor analytics events are immutable';
END;
$$;
