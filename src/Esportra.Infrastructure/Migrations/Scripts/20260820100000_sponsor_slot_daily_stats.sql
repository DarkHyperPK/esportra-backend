CREATE TABLE IF NOT EXISTS public.sponsor_slot_daily_stats (
    sponsor_id     UUID   NOT NULL REFERENCES public.sponsors(id) ON DELETE CASCADE,
    stat_date      DATE   NOT NULL,
    tournament_id  UUID   NOT NULL REFERENCES public.tournaments(id) ON DELETE CASCADE,
    placement_zone TEXT   NOT NULL,
    impressions    BIGINT NOT NULL DEFAULT 0,
    clicks         BIGINT NOT NULL DEFAULT 0,
    PRIMARY KEY (sponsor_id, stat_date, tournament_id, placement_zone)
);

ALTER TABLE public.sponsor_slot_daily_stats ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.sponsor_slot_daily_stats FORCE ROW LEVEL SECURITY;
REVOKE ALL ON public.sponsor_slot_daily_stats FROM anon, authenticated;
GRANT ALL ON public.sponsor_slot_daily_stats TO service_role;
