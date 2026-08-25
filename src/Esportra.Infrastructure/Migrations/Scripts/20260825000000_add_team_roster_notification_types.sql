-- Add roster-change notification types for live team sync
-- (used by TeamNotifications.InsertAsync from team member remove / leave / captaincy transfer flows)
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'team_member_removed';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'team_roster_updated';
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'team_captain_changed';
