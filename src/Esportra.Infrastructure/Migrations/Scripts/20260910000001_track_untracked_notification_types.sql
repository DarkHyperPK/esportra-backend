-- Register notification_type enum values that exist in production code but were never
-- formally tracked via a DbUp migration. All ADD VALUE statements are idempotent
-- (no-op on environments that already have the value).
-- Note: ALTER TYPE ADD VALUE cannot run inside a transaction in Postgres — no BEGIN/COMMIT.
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'checkin_open';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'party_code_submitted';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'scheduling_escalation';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'time_proposal_received';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'time_proposal_accepted';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'time_proposal_rejected';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'time_proposal_countered';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'br_round_active';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'dispute_reopened';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'dispute_player_reopened';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'check_in_reminder';
