-- CI post-baseline replay journal seed (generated — do not apply in production)
-- Regenerate: python .github/scripts/generate-replay-journal-seed.py
--
-- Marks pre-baseline DbUp scripts as already applied so migration-replay only
-- tests incremental migrations after 20260317_001_baseline.sql.

CREATE TABLE IF NOT EXISTS schemaversions (
  schemaversionsid SERIAL PRIMARY KEY,
  scriptname TEXT NOT NULL,
  applied TIMESTAMPTZ NOT NULL
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260228233709_fix_tournament_status_trigger.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260228233709_fix_tournament_status_trigger.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260301000000_restore_handle_new_user_trigger.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260301000000_restore_handle_new_user_trigger.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260301000001_fix_team_invitations_rls.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260301000001_fix_team_invitations_rls.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260302000000_add_faceit_accounts.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260302000000_add_faceit_accounts.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260303100000_venue_overhaul.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260303100000_venue_overhaul.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260303110000_license_system.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260303110000_license_system.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260303120000_venue_impressions_and_geo.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260303120000_venue_impressions_and_geo.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260303130000_venue_live_status.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260303130000_venue_live_status.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260303140000_backfill_licenses.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260303140000_backfill_licenses.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260304000000_enable_veto_realtime.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260304000000_enable_veto_realtime.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260304000001_finalize_match_locked_optional_scores.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260304000001_finalize_match_locked_optional_scores.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260304000002_enable_captain_page_realtime.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260304000002_enable_captain_page_realtime.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260304000003_match_disputes_and_dispute_rls.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260304000003_match_disputes_and_dispute_rls.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260304000004_file_match_result_dispute_rpc.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260304000004_file_match_result_dispute_rpc.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260304000005_match_ready_notification_trigger.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260304000005_match_ready_notification_trigger.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260304000006_notifications_link_and_new_types.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260304000006_notifications_link_and_new_types.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260304000007_dispute_storage_buckets.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260304000007_dispute_storage_buckets.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260304000008_dispute_comments_rls_fix.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260304000008_dispute_comments_rls_fix.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260304000009_notify_admins_rpc.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260304000009_notify_admins_rpc.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260304000010_seed_admin_roles.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260304000010_seed_admin_roles.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260313000000_games_metadata_cache.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260313000000_games_metadata_cache.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260313000001_veto_game_column_and_notification_enum.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260313000001_veto_game_column_and_notification_enum.sql'
);

INSERT INTO schemaversions (scriptname, applied)
SELECT 'Esportra.Infrastructure.Migrations.Scripts.20260317_001_baseline.sql', NOW()
WHERE NOT EXISTS (
  SELECT 1 FROM schemaversions WHERE scriptname = 'Esportra.Infrastructure.Migrations.Scripts.20260317_001_baseline.sql'
);

SELECT 'replay journal seed applied (23 scripts)' AS status;
