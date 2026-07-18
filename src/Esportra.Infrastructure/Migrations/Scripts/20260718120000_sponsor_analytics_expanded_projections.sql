-- Placement daily aggregates (fixed cardinality: 5 placement types)
CREATE TABLE IF NOT EXISTS public.sponsor_placement_daily_stats (
    sponsor_id UUID NOT NULL REFERENCES public.sponsors(id) ON DELETE CASCADE,
    stat_date DATE NOT NULL,
    placement TEXT NOT NULL,
    impressions BIGINT NOT NULL DEFAULT 0,
    clicks BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (sponsor_id, stat_date, placement)
);

CREATE INDEX IF NOT EXISTS sponsor_placement_daily_stats_report_idx
    ON public.sponsor_placement_daily_stats (sponsor_id, stat_date)
    INCLUDE (placement, impressions, clicks);

-- Device class daily aggregates (fixed cardinality: 4 device classes)
CREATE TABLE IF NOT EXISTS public.sponsor_device_daily_stats (
    sponsor_id UUID NOT NULL REFERENCES public.sponsors(id) ON DELETE CASCADE,
    stat_date DATE NOT NULL,
    device_class TEXT NOT NULL CHECK (device_class IN ('mobile-web', 'desktop-web', 'tablet-web', 'unknown-web')),
    impressions BIGINT NOT NULL DEFAULT 0,
    clicks BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (sponsor_id, stat_date, device_class)
);

CREATE INDEX IF NOT EXISTS sponsor_device_daily_stats_report_idx
    ON public.sponsor_device_daily_stats (sponsor_id, stat_date)
    INCLUDE (device_class, impressions, clicks);

-- Content attribution daily aggregates (tournament + page path)
CREATE TABLE IF NOT EXISTS public.sponsor_content_daily_stats (
    sponsor_id UUID NOT NULL REFERENCES public.sponsors(id) ON DELETE CASCADE,
    stat_date DATE NOT NULL,
    tournament_id UUID REFERENCES public.tournaments(id) ON DELETE SET NULL,
    page_path TEXT,
    impressions BIGINT NOT NULL DEFAULT 0,
    clicks BIGINT NOT NULL DEFAULT 0,
    CONSTRAINT sponsor_content_daily_stats_pk
        UNIQUE (sponsor_id, stat_date, tournament_id, page_path)
);

CREATE INDEX IF NOT EXISTS sponsor_content_daily_stats_tournament_idx
    ON public.sponsor_content_daily_stats (sponsor_id, stat_date, tournament_id)
    WHERE tournament_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS sponsor_content_daily_stats_page_idx
    ON public.sponsor_content_daily_stats (sponsor_id, stat_date, page_path)
    WHERE page_path IS NOT NULL;

-- Export tracking table
CREATE TABLE IF NOT EXISTS public.sponsor_analytics_exports (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    sponsor_id UUID NOT NULL REFERENCES public.sponsors(id) ON DELETE CASCADE,
    requested_by UUID NOT NULL,
    report_type TEXT NOT NULL CHECK (report_type IN ('summary', 'performance', 'placements', 'content', 'devices', 'full')),
    period_days INT NOT NULL CHECK (period_days IN (7, 30, 90)),
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'processing', 'completed', 'failed', 'expired')),
    file_path TEXT,
    signed_url TEXT,
    url_expires_at TIMESTAMPTZ,
    requested_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at TIMESTAMPTZ,
    error_message TEXT
);

CREATE INDEX IF NOT EXISTS sponsor_analytics_exports_lookup_idx
    ON public.sponsor_analytics_exports (sponsor_id, requested_by, status);

CREATE INDEX IF NOT EXISTS sponsor_analytics_exports_pending_idx
    ON public.sponsor_analytics_exports (status, requested_at)
    WHERE status IN ('pending', 'processing');

-- RLS for all new tables
DO $$
DECLARE table_name TEXT;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'sponsor_placement_daily_stats',
        'sponsor_device_daily_stats',
        'sponsor_content_daily_stats',
        'sponsor_analytics_exports'
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
