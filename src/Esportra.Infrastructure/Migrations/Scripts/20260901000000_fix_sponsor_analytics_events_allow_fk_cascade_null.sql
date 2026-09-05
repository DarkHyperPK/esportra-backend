-- The BEFORE UPDATE trigger on sponsor_analytics_events blocked FK cascade SET NULL
-- when a referenced tournament is deleted. Postgres issues an UPDATE to nullify
-- tournament_id, which triggered the immutability guard unconditionally.
-- Fix: allow the update only when tournament_id is being set from non-null to null
-- and every other column is unchanged (exactly what ON DELETE SET NULL does).
CREATE OR REPLACE FUNCTION public.reject_sponsor_analytics_event_update()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.tournament_id IS NOT NULL
       AND NEW.tournament_id IS NULL
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
