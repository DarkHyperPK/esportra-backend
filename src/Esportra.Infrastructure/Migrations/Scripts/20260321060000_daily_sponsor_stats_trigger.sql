-- Create daily_sponsor_stats table if it doesn't exist
CREATE TABLE IF NOT EXISTS daily_sponsor_stats (
    id          uuid DEFAULT gen_random_uuid() PRIMARY KEY,
    sponsor_id  uuid NOT NULL REFERENCES sponsors(id) ON DELETE CASCADE,
    stat_date   date NOT NULL DEFAULT CURRENT_DATE,
    impressions bigint NOT NULL DEFAULT 0,
    clicks      bigint NOT NULL DEFAULT 0,
    unique_impressions bigint NOT NULL DEFAULT 0,
    UNIQUE (sponsor_id, stat_date)
);

CREATE INDEX IF NOT EXISTS idx_daily_sponsor_stats_sponsor_date
    ON daily_sponsor_stats (sponsor_id, stat_date);

-- Trigger function: upsert daily_sponsor_stats on each impression insert
CREATE OR REPLACE FUNCTION fn_aggregate_sponsor_impression()
RETURNS TRIGGER
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
    INSERT INTO daily_sponsor_stats (sponsor_id, stat_date, impressions, clicks, unique_impressions)
    VALUES (
        NEW.sponsor_id,
        CURRENT_DATE,
        CASE WHEN NEW.event_type = 'impression' THEN 1 ELSE 0 END,
        CASE WHEN NEW.event_type = 'click' THEN 1 ELSE 0 END,
        CASE WHEN NEW.event_type = 'impression' AND NEW.visitor_id IS NOT NULL THEN 1 ELSE 0 END
    )
    ON CONFLICT (sponsor_id, stat_date)
    DO UPDATE SET
        impressions = daily_sponsor_stats.impressions
            + CASE WHEN NEW.event_type = 'impression' THEN 1 ELSE 0 END,
        clicks = daily_sponsor_stats.clicks
            + CASE WHEN NEW.event_type = 'click' THEN 1 ELSE 0 END,
        unique_impressions = daily_sponsor_stats.unique_impressions
            + CASE WHEN NEW.event_type = 'impression' AND NEW.visitor_id IS NOT NULL THEN 1 ELSE 0 END;

    RETURN NEW;
END;
$$;

-- Attach trigger to sponsor_impressions table
DROP TRIGGER IF EXISTS trg_aggregate_sponsor_impression ON sponsor_impressions;
CREATE TRIGGER trg_aggregate_sponsor_impression
    AFTER INSERT ON sponsor_impressions
    FOR EACH ROW
    EXECUTE FUNCTION fn_aggregate_sponsor_impression();

-- RLS on daily_sponsor_stats
ALTER TABLE daily_sponsor_stats ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE tablename = 'daily_sponsor_stats' AND policyname = 'admin_read_daily_stats'
  ) THEN
    CREATE POLICY "admin_read_daily_stats" ON daily_sponsor_stats FOR SELECT USING (true);
  END IF;
END $$;
