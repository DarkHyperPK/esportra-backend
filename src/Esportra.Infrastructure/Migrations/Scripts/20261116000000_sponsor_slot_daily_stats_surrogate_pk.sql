-- sponsor_slot_daily_stats previously used (sponsor_id, stat_date, tournament_id, placement_zone)
-- as its PK, which forced ON DELETE RESTRICT and made permanent tournament deletion fail.
-- Switch to a surrogate PK so rows survive tournament deletion (tournament_id → NULL via SET NULL),
-- matching the pattern already used by sponsor_analytics_events and sponsor_content_daily_stats.

-- 1. Add surrogate PK column.
ALTER TABLE public.sponsor_slot_daily_stats
    ADD COLUMN IF NOT EXISTS id BIGINT GENERATED ALWAYS AS IDENTITY;

-- 2. Drop the composite PK that included tournament_id.
ALTER TABLE public.sponsor_slot_daily_stats
    DROP CONSTRAINT IF EXISTS sponsor_slot_daily_stats_pkey;

-- 3. Promote the surrogate to PK.
ALTER TABLE public.sponsor_slot_daily_stats
    ADD PRIMARY KEY (id);

-- 4. Enforce natural-key uniqueness at write time via a partial unique index.
--    tournament_id is always NOT NULL during writes; rows whose tournament was deleted
--    have NULL and are intentionally left unkeyed (they cannot conflict with new writes).
CREATE UNIQUE INDEX IF NOT EXISTS sponsor_slot_daily_stats_natural_key
    ON public.sponsor_slot_daily_stats (sponsor_id, stat_date, tournament_id, placement_zone)
    WHERE tournament_id IS NOT NULL;

-- 5. Backfill tournament_name for any rows that predate the snapshot column.
UPDATE public.sponsor_slot_daily_stats s
SET    tournament_name = t.name
FROM   public.tournaments t
WHERE  s.tournament_id = t.id
  AND  s.tournament_name IS NULL;

-- 6. Replace RESTRICT FK with SET NULL so row attribution is preserved on deletion.
ALTER TABLE public.sponsor_slot_daily_stats
    DROP CONSTRAINT IF EXISTS sponsor_slot_daily_stats_tournament_id_fkey;

ALTER TABLE public.sponsor_slot_daily_stats
    ADD CONSTRAINT sponsor_slot_daily_stats_tournament_id_fkey
    FOREIGN KEY (tournament_id) REFERENCES public.tournaments(id) ON DELETE SET NULL;
