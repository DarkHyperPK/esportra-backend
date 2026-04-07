-- ============================================================
-- Fix: service_role GRANT ALL on venue-related tables
--
-- Problem:  Multiple venue tables return 403 / 42501
--           "permission denied for table <name>" when accessed
--           via the service_role key (Supabase backend).
--
-- Root cause: RLS policies were created correctly, but the
--             underlying PostgreSQL table-level GRANT was never
--             issued. service_role bypasses RLS but still needs
--             a table-level privilege to touch the relation.
--
-- Scope:    12 tables that were missing grants.
--           station_health_snapshots already has grants
--           (20260404133500) — intentionally excluded.
--
-- GRANT is idempotent — re-running is safe; duplicate grants
-- are silently ignored by PostgreSQL.
-- ============================================================


-- ===================
-- 1. venue_stations
-- ===================
GRANT ALL    ON venue_stations TO service_role;
GRANT SELECT ON venue_stations TO authenticated;

-- ===================
-- 2. venue_staff
-- ===================
GRANT ALL    ON venue_staff TO service_role;
GRANT SELECT ON venue_staff TO authenticated;

-- ===================
-- 3. venue_staff_invites
-- ===================
GRANT ALL    ON venue_staff_invites TO service_role;
GRANT SELECT ON venue_staff_invites TO authenticated;

-- ===================
-- 4. venue_sessions
--    authenticated users can start/update their own sessions
-- ===================
GRANT ALL                    ON venue_sessions TO service_role;
GRANT SELECT, INSERT, UPDATE ON venue_sessions TO authenticated;

-- ===================
-- 5. venue_session_events
-- ===================
GRANT ALL    ON venue_session_events TO service_role;
GRANT SELECT ON venue_session_events TO authenticated;

-- ===================
-- 6. venue_billing_config
-- ===================
GRANT ALL    ON venue_billing_config TO service_role;
GRANT SELECT ON venue_billing_config TO authenticated;

-- ===================
-- 7. venue_receipts
-- ===================
GRANT ALL    ON venue_receipts TO service_role;
GRANT SELECT ON venue_receipts TO authenticated;

-- ===================
-- 8. venue_live_status
-- ===================
GRANT ALL    ON venue_live_status TO service_role;
GRANT SELECT ON venue_live_status TO authenticated;

-- ===================
-- 9. venue_availability_snapshot
-- ===================
GRANT ALL    ON venue_availability_snapshot TO service_role;
GRANT SELECT ON venue_availability_snapshot TO authenticated;

-- ===================
-- 10. venue_impressions
--     authenticated users record their own impressions
-- ===================
GRANT ALL                    ON venue_impressions TO service_role;
GRANT SELECT, INSERT, UPDATE ON venue_impressions TO authenticated;

-- ===================
-- 11. venue_bookings
--     authenticated users create and manage their own bookings
-- ===================
GRANT ALL                    ON venue_bookings TO service_role;
GRANT SELECT, INSERT, UPDATE ON venue_bookings TO authenticated;

-- ===================
-- 12. licenses
-- ===================
GRANT ALL    ON licenses TO service_role;
GRANT SELECT ON licenses TO authenticated;


-- ============================================================
-- Sequences: ensure service_role can use any auto-increment /
-- SERIAL / GENERATED columns on public-schema tables.
-- This is a blanket grant — safe and idempotent.
-- ============================================================
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO service_role;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO authenticated;
