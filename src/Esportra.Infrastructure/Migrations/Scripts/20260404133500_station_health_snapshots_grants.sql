-- ============================================================
-- Station Health Snapshots — Table-level Grants
--
-- Fixes: 42501 "permission denied for table station_health_snapshots"
-- Root cause: the table was created without GRANT statements, so
-- even service_role (which bypasses RLS) lacks the underlying
-- PostgreSQL privilege to INSERT.
--
-- GRANT is idempotent — re-running is safe; duplicate grants
-- are silently ignored by PostgreSQL.
-- ============================================================

-- 1. service_role: full access (SyncService / backend hub agent)
GRANT ALL ON station_health_snapshots TO service_role;

-- 2. authenticated: venue operators read dashboards, hub agents
--    that authenticate as a user may push snapshots
GRANT SELECT, INSERT ON station_health_snapshots TO authenticated;

-- 3. anon: read-only for public API / unauthenticated dashboard views
--    NOTE: INSERT deliberately excluded — anonymous users must not
--    push telemetry data. If the desktop hub agent needs to write,
--    it should authenticate (service_role key or user JWT).
GRANT SELECT ON station_health_snapshots TO anon;
