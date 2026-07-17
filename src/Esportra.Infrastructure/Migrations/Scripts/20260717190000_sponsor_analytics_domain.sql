CREATE TABLE IF NOT EXISTS public.sponsor_audience_identities (
    sponsor_id UUID NOT NULL REFERENCES public.sponsors(id) ON DELETE CASCADE,
    identity_lookup BYTEA NOT NULL,
    identity_key_version SMALLINT NOT NULL,
    identity_kind TEXT NOT NULL CHECK (identity_kind IN ('authenticated', 'anonymous')),
    audience_id UUID NOT NULL DEFAULT gen_random_uuid(),
    first_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (sponsor_id, identity_lookup),
    UNIQUE (sponsor_id, audience_id),
    CHECK (octet_length(identity_lookup) = 32),
    CHECK (expires_at > last_seen_at)
);

CREATE INDEX IF NOT EXISTS sponsor_audience_identities_expiry_idx
    ON public.sponsor_audience_identities (expires_at);

CREATE TABLE IF NOT EXISTS public.sponsor_analytics_events (
    event_sequence BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id UUID NOT NULL,
    sponsor_id UUID NOT NULL REFERENCES public.sponsors(id) ON DELETE RESTRICT,
    audience_id UUID NOT NULL,
    identity_kind TEXT NOT NULL CHECK (identity_kind IN ('authenticated', 'anonymous')),
    event_type TEXT NOT NULL CHECK (event_type IN ('impression', 'click')),
    placement TEXT NOT NULL,
    tournament_id UUID REFERENCES public.tournaments(id) ON DELETE SET NULL,
    page_path TEXT,
    country_code TEXT,
    country_provenance TEXT NOT NULL CHECK (country_provenance IN ('profile_self_reported', 'geoip', 'unknown')),
    age_band TEXT CHECK (age_band IN ('13_17', '18_24', '25_34', '35_44', '45_54', '55_plus')),
    age_provenance TEXT NOT NULL CHECK (age_provenance IN ('profile_self_reported', 'unknown')),
    schema_version SMALLINT NOT NULL DEFAULT 1,
    received_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    event_date_utc DATE NOT NULL,
    UNIQUE (sponsor_id, event_id),
    CHECK (country_code IS NULL OR country_code ~ '^[A-Z]{2}$'),
    CHECK ((country_code IS NULL) = (country_provenance = 'unknown')),
    CHECK ((age_band IS NULL) = (age_provenance = 'unknown'))
);

CREATE INDEX IF NOT EXISTS sponsor_analytics_events_report_idx
    ON public.sponsor_analytics_events (sponsor_id, event_date_utc, event_type, audience_id);
CREATE INDEX IF NOT EXISTS sponsor_analytics_events_retention_idx
    ON public.sponsor_analytics_events (received_at);

CREATE TABLE IF NOT EXISTS public.sponsor_audience_daily_facts (
    sponsor_id UUID NOT NULL REFERENCES public.sponsors(id) ON DELETE CASCADE,
    fact_date DATE NOT NULL,
    audience_id UUID NOT NULL,
    event_type TEXT NOT NULL CHECK (event_type IN ('impression', 'click')),
    event_count BIGINT NOT NULL CHECK (event_count > 0),
    first_event_sequence BIGINT NOT NULL,
    last_event_sequence BIGINT NOT NULL,
    country_code TEXT,
    country_provenance TEXT NOT NULL CHECK (country_provenance IN ('profile_self_reported', 'geoip', 'unknown')),
    age_band TEXT CHECK (age_band IN ('13_17', '18_24', '25_34', '35_44', '45_54', '55_plus')),
    age_provenance TEXT NOT NULL CHECK (age_provenance IN ('profile_self_reported', 'unknown')),
    PRIMARY KEY (sponsor_id, fact_date, audience_id, event_type)
);

CREATE INDEX IF NOT EXISTS sponsor_audience_daily_facts_report_idx
    ON public.sponsor_audience_daily_facts (sponsor_id, event_type, fact_date, audience_id)
    INCLUDE (event_count, last_event_sequence, country_code, country_provenance, age_band, age_provenance);

CREATE TABLE IF NOT EXISTS public.sponsor_daily_totals (
    sponsor_id UUID NOT NULL REFERENCES public.sponsors(id) ON DELETE CASCADE,
    stat_date DATE NOT NULL,
    impressions BIGINT NOT NULL DEFAULT 0,
    clicks BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (sponsor_id, stat_date)
);

CREATE OR REPLACE FUNCTION public.reject_sponsor_analytics_event_update()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'sponsor analytics events are immutable';
END;
$$;

DROP TRIGGER IF EXISTS sponsor_analytics_events_no_update ON public.sponsor_analytics_events;
CREATE TRIGGER sponsor_analytics_events_no_update
    BEFORE UPDATE ON public.sponsor_analytics_events
    FOR EACH ROW EXECUTE FUNCTION public.reject_sponsor_analytics_event_update();

DO $$
DECLARE table_name TEXT;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'sponsor_audience_identities',
        'sponsor_analytics_events',
        'sponsor_audience_daily_facts',
        'sponsor_daily_totals'
    ]
    LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', table_name);
        EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', table_name);
        EXECUTE format('REVOKE ALL ON public.%I FROM anon, authenticated', table_name);
        EXECUTE format('DROP POLICY IF EXISTS %I ON public.%I', table_name || '_service_role_all', table_name);
        EXECUTE format(
            'CREATE POLICY %I ON public.%I FOR ALL TO service_role USING (true) WITH CHECK (true)',
            table_name || '_service_role_all', table_name);
        EXECUTE format('GRANT ALL ON public.%I TO service_role', table_name);
    END LOOP;
END $$;

GRANT USAGE, SELECT ON SEQUENCE public.sponsor_analytics_events_event_sequence_seq TO service_role;
