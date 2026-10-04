-- Organizer alert when a screenshot report names players who aren't on the competitors' rosters
-- (ScoreboardReportVerifier.NotifyOrganizersAsync).
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'match_report_flag';
