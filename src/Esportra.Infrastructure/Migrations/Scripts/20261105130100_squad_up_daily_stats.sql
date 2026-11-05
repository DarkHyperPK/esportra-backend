-- Durable Squad Up counters for the Admin Centre. Sessions and swipes are ephemeral (deleted when a
-- session ends), so history lives here, one row per UTC day and region. Written only by the API.
CREATE TABLE IF NOT EXISTS public.squad_up_daily_stats (
    day              DATE NOT NULL,
    region           TEXT NOT NULL,
    sessions_started INT  NOT NULL DEFAULT 0,
    swipes_right     INT  NOT NULL DEFAULT 0,
    swipes_left      INT  NOT NULL DEFAULT 0,
    matches          INT  NOT NULL DEFAULT 0,
    PRIMARY KEY (day, region)
);

ALTER TABLE public.squad_up_daily_stats ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS squad_up_daily_stats_service ON public.squad_up_daily_stats;
CREATE POLICY squad_up_daily_stats_service ON public.squad_up_daily_stats
    FOR ALL TO service_role USING (true) WITH CHECK (true);
REVOKE ALL ON public.squad_up_daily_stats FROM anon, authenticated;
