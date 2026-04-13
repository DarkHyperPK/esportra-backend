-- ============================================================================
-- Migration: 20260412175200_venue_hub_heartbeat_columns.sql
-- Purpose:   Add columns to the venues table for Hub self-registration
--            and heartbeat tracking.
--
--   hub_lan_url        - The Hub's LAN URL (e.g. "http://192.168.1.100:5134"),
--                        reported by the Hub itself during registration/heartbeat.
--   hub_version        - The Hub software version string (e.g. "1.0.0").
--   hub_last_heartbeat - Timestamp of the last successful heartbeat from the Hub.
--
-- No RLS or GRANT changes needed — these columns live on the existing venues
-- table which already has RLS enabled and appropriate grants in place.
-- ============================================================================

ALTER TABLE venues ADD COLUMN IF NOT EXISTS hub_lan_url        TEXT;
ALTER TABLE venues ADD COLUMN IF NOT EXISTS hub_version        TEXT;
ALTER TABLE venues ADD COLUMN IF NOT EXISTS hub_last_heartbeat TIMESTAMPTZ;
