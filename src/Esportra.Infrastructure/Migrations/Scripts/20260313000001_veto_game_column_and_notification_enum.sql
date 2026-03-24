-- Add game column to match_map_vetos (backend stores game per veto session)
ALTER TABLE public.match_map_vetos ADD COLUMN IF NOT EXISTS game TEXT DEFAULT 'valorant';

-- Add team_invite_response to notification_type enum (used when accepting/rejecting team invites)
ALTER TYPE public.notification_type ADD VALUE IF NOT EXISTS 'team_invite_response';
