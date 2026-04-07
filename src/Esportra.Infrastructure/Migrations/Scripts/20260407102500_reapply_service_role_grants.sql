-- ============================================================
-- Re-apply: service_role GRANTs that silently failed
--
-- The original migration (20260407100000) ran as the `postgres`
-- user which is NOT a superuser in Supabase. GRANT executed
-- without error but emitted "no privileges were granted" because
-- postgres doesn't own the tables (supabase_admin does).
--
-- This migration re-applies the same GRANTs. It will now run via
-- the PostgresMigrations connection string (supabase_admin).
--
-- GRANT is idempotent — safe to re-run on environments where
-- the grants were already applied manually.
-- ============================================================

-- 1. venue_stations
GRANT ALL    ON venue_stations TO service_role;
GRANT SELECT ON venue_stations TO authenticated;

-- 2. venue_staff
GRANT ALL    ON venue_staff TO service_role;
GRANT SELECT ON venue_staff TO authenticated;

-- 3. venue_staff_invites
GRANT ALL    ON venue_staff_invites TO service_role;
GRANT SELECT ON venue_staff_invites TO authenticated;

-- 4. venue_sessions
GRANT ALL                    ON venue_sessions TO service_role;
GRANT SELECT, INSERT, UPDATE ON venue_sessions TO authenticated;

-- 5. venue_session_events
GRANT ALL    ON venue_session_events TO service_role;
GRANT SELECT ON venue_session_events TO authenticated;

-- 6. venue_billing_config
GRANT ALL    ON venue_billing_config TO service_role;
GRANT SELECT ON venue_billing_config TO authenticated;

-- 7. venue_receipts
GRANT ALL    ON venue_receipts TO service_role;
GRANT SELECT ON venue_receipts TO authenticated;

-- 8. venue_live_status
GRANT ALL    ON venue_live_status TO service_role;
GRANT SELECT ON venue_live_status TO authenticated;

-- 9. venue_availability_snapshot
GRANT ALL    ON venue_availability_snapshot TO service_role;
GRANT SELECT ON venue_availability_snapshot TO authenticated;

-- 10. venue_impressions
GRANT ALL                    ON venue_impressions TO service_role;
GRANT SELECT, INSERT, UPDATE ON venue_impressions TO authenticated;

-- 11. venue_bookings
GRANT ALL                    ON venue_bookings TO service_role;
GRANT SELECT, INSERT, UPDATE ON venue_bookings TO authenticated;

-- 12. licenses
GRANT ALL    ON licenses TO service_role;
GRANT SELECT ON licenses TO authenticated;

-- Sequences
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO service_role;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO authenticated;
