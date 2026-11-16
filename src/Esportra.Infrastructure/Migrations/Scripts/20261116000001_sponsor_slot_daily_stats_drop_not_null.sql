-- The surrogate PK migration (20261116000000) changed the FK to SET NULL but did not
-- drop the NOT NULL constraint on tournament_id, so the cascade still fails at deletion.
ALTER TABLE public.sponsor_slot_daily_stats
    ALTER COLUMN tournament_id DROP NOT NULL;
