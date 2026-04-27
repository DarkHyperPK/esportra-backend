-- ============================================================================
-- 20260413210000_analytics_and_activity_log.sql
-- Pre-aggregated daily stats for fast venue analytics + activity audit log
-- ============================================================================

-- ═══════════════════════════════════════════════════════════════════════════════
-- 1. daily_stats — Pre-aggregated daily metrics per venue
-- ═══════════════════════════════════════════════════════════════════════════════

CREATE TABLE IF NOT EXISTS daily_stats (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    venue_id            UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
    date                DATE NOT NULL,
    total_sessions      INT NOT NULL DEFAULT 0,
    total_hours         NUMERIC(10,2) NOT NULL DEFAULT 0,
    session_revenue     NUMERIC(10,2) NOT NULL DEFAULT 0,
    pos_revenue         NUMERIC(10,2) NOT NULL DEFAULT 0,
    package_revenue     NUMERIC(10,2) NOT NULL DEFAULT 0,
    total_revenue       NUMERIC(10,2) NOT NULL DEFAULT 0,
    unique_members      INT NOT NULL DEFAULT 0,
    new_members         INT NOT NULL DEFAULT 0,
    walk_ins            INT NOT NULL DEFAULT 0,
    avg_session_minutes NUMERIC(8,2) NOT NULL DEFAULT 0,
    peak_hour           INT,                         -- 0-23, hour with most sessions
    avg_utilization     NUMERIC(5,2) NOT NULL DEFAULT 0, -- percentage
    total_orders        INT NOT NULL DEFAULT 0,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(venue_id, date)
);

ALTER TABLE daily_stats ENABLE ROW LEVEL SECURITY;

-- Owner or active staff can read their venue's stats
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'daily_stats_owner_staff_select' AND tablename = 'daily_stats'
  ) THEN
    CREATE POLICY "daily_stats_owner_staff_select" ON daily_stats
      FOR SELECT USING (
        EXISTS (SELECT 1 FROM venues WHERE venues.id = daily_stats.venue_id AND venues.owner_id = auth.uid())
        OR EXISTS (SELECT 1 FROM venue_staff vs WHERE vs.venue_id = daily_stats.venue_id AND vs.user_id = auth.uid() AND vs.status = 'active')
      );
  END IF;
END; $$;

-- No direct inserts/updates from authenticated — only via SECURITY DEFINER RPC
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'daily_stats_service_role_all' AND tablename = 'daily_stats'
  ) THEN
    CREATE POLICY "daily_stats_service_role_all" ON daily_stats
      FOR ALL USING (true) WITH CHECK (true);
  END IF;
END; $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON daily_stats TO service_role;
GRANT SELECT ON daily_stats TO authenticated;

-- Indexes
CREATE INDEX IF NOT EXISTS idx_daily_stats_venue_date ON daily_stats (venue_id, date DESC);

-- updated_at trigger
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_daily_stats_updated_at') THEN
    CREATE TRIGGER trg_daily_stats_updated_at
      BEFORE UPDATE ON daily_stats
      FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();
  END IF;
END; $$;


-- ═══════════════════════════════════════════════════════════════════════════════
-- 2. activity_log — Audit trail for venue actions
-- ═══════════════════════════════════════════════════════════════════════════════

CREATE TABLE IF NOT EXISTS activity_log (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    venue_id    UUID NOT NULL REFERENCES venues(id) ON DELETE CASCADE,
    actor_id    UUID,          -- who did it (staff/owner user id)
    actor_name  TEXT,          -- denormalized for display
    action      TEXT NOT NULL, -- 'session.start', 'session.end', 'member.ban', 'order.create', etc.
    target_type TEXT,          -- 'session', 'member', 'station', 'order', 'booking'
    target_id   TEXT,          -- the affected entity id
    details     JSONB DEFAULT '{}'::jsonb, -- extra context
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE activity_log ENABLE ROW LEVEL SECURITY;

-- Owner or active staff can read their venue's activity log
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'activity_log_owner_staff_select' AND tablename = 'activity_log'
  ) THEN
    CREATE POLICY "activity_log_owner_staff_select" ON activity_log
      FOR SELECT USING (
        EXISTS (SELECT 1 FROM venues WHERE venues.id = activity_log.venue_id AND venues.owner_id = auth.uid())
        OR EXISTS (SELECT 1 FROM venue_staff vs WHERE vs.venue_id = activity_log.venue_id AND vs.user_id = auth.uid() AND vs.status = 'active')
      );
  END IF;
END; $$;

-- Owner or active staff can insert activity logs
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'activity_log_owner_staff_insert' AND tablename = 'activity_log'
  ) THEN
    CREATE POLICY "activity_log_owner_staff_insert" ON activity_log
      FOR INSERT WITH CHECK (
        EXISTS (SELECT 1 FROM venues WHERE venues.id = activity_log.venue_id AND venues.owner_id = auth.uid())
        OR EXISTS (SELECT 1 FROM venue_staff vs WHERE vs.venue_id = activity_log.venue_id AND vs.user_id = auth.uid() AND vs.status = 'active')
      );
  END IF;
END; $$;

-- Service role full access
DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_policies WHERE policyname = 'activity_log_service_role_all' AND tablename = 'activity_log'
  ) THEN
    CREATE POLICY "activity_log_service_role_all" ON activity_log
      FOR ALL USING (true) WITH CHECK (true);
  END IF;
END; $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON activity_log TO service_role;
GRANT SELECT, INSERT ON activity_log TO authenticated;

-- Indexes
CREATE INDEX IF NOT EXISTS idx_activity_log_venue_created ON activity_log (venue_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_activity_log_venue_action  ON activity_log (venue_id, action);


-- ═══════════════════════════════════════════════════════════════════════════════
-- 3. refresh_daily_stats — SECURITY DEFINER aggregation function
-- ═══════════════════════════════════════════════════════════════════════════════

CREATE OR REPLACE FUNCTION refresh_daily_stats(p_venue_id UUID, p_date DATE)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_total_sessions      INT;
    v_total_hours         NUMERIC(10,2);
    v_session_revenue     NUMERIC(10,2);
    v_pos_revenue         NUMERIC(10,2);
    v_package_revenue     NUMERIC(10,2);
    v_unique_members      INT;
    v_new_members         INT;
    v_walk_ins            INT;
    v_avg_session_minutes NUMERIC(8,2);
    v_peak_hour           INT;
    v_avg_utilization     NUMERIC(5,2);
    v_total_orders        INT;
    v_total_stations      INT;
BEGIN
    -- Verify caller is venue owner or active staff
    IF NOT EXISTS (
        SELECT 1 FROM venues WHERE id = p_venue_id AND owner_id = auth.uid()
    ) AND NOT EXISTS (
        SELECT 1 FROM venue_staff WHERE venue_id = p_venue_id AND user_id = auth.uid() AND status = 'active'
    ) THEN
        RAISE EXCEPTION 'Unauthorized: not owner or staff of this venue';
    END IF;

    -- ── Session stats ──────────────────────────────────────────
    SELECT
        COUNT(*),
        COALESCE(SUM(
            EXTRACT(EPOCH FROM (COALESCE(ended_at, NOW()) - started_at)) / 3600
        ), 0),
        COALESCE(SUM(total_charged), 0),
        COALESCE(AVG(
            EXTRACT(EPOCH FROM (COALESCE(ended_at, NOW()) - started_at)) / 60
        ), 0)
    INTO v_total_sessions, v_total_hours, v_session_revenue, v_avg_session_minutes
    FROM venue_sessions
    WHERE venue_id = p_venue_id
      AND started_at >= p_date
      AND started_at < p_date + INTERVAL '1 day';

    -- ── POS revenue ────────────────────────────────────────────
    SELECT COALESCE(SUM(total), 0), COUNT(*)
    INTO v_pos_revenue, v_total_orders
    FROM pos_orders
    WHERE venue_id = p_venue_id
      AND created_at >= p_date
      AND created_at < p_date + INTERVAL '1 day'
      AND status != 'cancelled';

    -- ── Package revenue (purchased that day) ───────────────────
    SELECT COALESCE(SUM(vp.price), 0)
    INTO v_package_revenue
    FROM member_packages mp
    JOIN venue_packages vp ON vp.id = mp.package_id
    WHERE mp.venue_id = p_venue_id
      AND mp.purchased_at >= p_date
      AND mp.purchased_at < p_date + INTERVAL '1 day';

    -- ── Unique members who had sessions ────────────────────────
    SELECT COUNT(DISTINCT member_id)
    INTO v_unique_members
    FROM venue_sessions
    WHERE venue_id = p_venue_id
      AND started_at >= p_date
      AND started_at < p_date + INTERVAL '1 day'
      AND member_id IS NOT NULL;

    -- ── New members created that day ───────────────────────────
    SELECT COUNT(*)
    INTO v_new_members
    FROM members
    WHERE venue_id = p_venue_id
      AND created_at >= p_date
      AND created_at < p_date + INTERVAL '1 day';

    -- ── Walk-ins (sessions without member) ─────────────────────
    SELECT COUNT(*)
    INTO v_walk_ins
    FROM venue_sessions
    WHERE venue_id = p_venue_id
      AND started_at >= p_date
      AND started_at < p_date + INTERVAL '1 day'
      AND member_id IS NULL;

    -- ── Peak hour ──────────────────────────────────────────────
    SELECT EXTRACT(HOUR FROM started_at)::INT
    INTO v_peak_hour
    FROM venue_sessions
    WHERE venue_id = p_venue_id
      AND started_at >= p_date
      AND started_at < p_date + INTERVAL '1 day'
    GROUP BY EXTRACT(HOUR FROM started_at)
    ORDER BY COUNT(*) DESC
    LIMIT 1;

    -- ── Utilization (% of station-hours used) ──────────────────
    SELECT COUNT(*) INTO v_total_stations
    FROM venue_stations
    WHERE venue_id = p_venue_id;

    IF v_total_stations > 0 THEN
        -- Max possible hours = stations × 24
        v_avg_utilization := LEAST(
            ROUND((v_total_hours / (v_total_stations * 24.0)) * 100, 2),
            100.0
        );
    ELSE
        v_avg_utilization := 0;
    END IF;

    -- ── Upsert the daily_stats row ─────────────────────────────
    INSERT INTO daily_stats (
        venue_id, date,
        total_sessions, total_hours, session_revenue,
        pos_revenue, package_revenue, total_revenue,
        unique_members, new_members, walk_ins,
        avg_session_minutes, peak_hour, avg_utilization,
        total_orders
    ) VALUES (
        p_venue_id, p_date,
        v_total_sessions, v_total_hours, v_session_revenue,
        v_pos_revenue, v_package_revenue,
        v_session_revenue + v_pos_revenue + v_package_revenue,
        v_unique_members, v_new_members, v_walk_ins,
        v_avg_session_minutes, v_peak_hour, v_avg_utilization,
        v_total_orders
    )
    ON CONFLICT (venue_id, date) DO UPDATE SET
        total_sessions      = EXCLUDED.total_sessions,
        total_hours         = EXCLUDED.total_hours,
        session_revenue     = EXCLUDED.session_revenue,
        pos_revenue         = EXCLUDED.pos_revenue,
        package_revenue     = EXCLUDED.package_revenue,
        total_revenue       = EXCLUDED.total_revenue,
        unique_members      = EXCLUDED.unique_members,
        new_members         = EXCLUDED.new_members,
        walk_ins            = EXCLUDED.walk_ins,
        avg_session_minutes = EXCLUDED.avg_session_minutes,
        peak_hour           = EXCLUDED.peak_hour,
        avg_utilization     = EXCLUDED.avg_utilization,
        total_orders        = EXCLUDED.total_orders,
        updated_at          = now();
END;
$$;
