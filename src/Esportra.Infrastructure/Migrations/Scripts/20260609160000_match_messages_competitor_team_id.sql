-- Bracket slot IDs (team or solo participant) are stored in match_messages.team_id.
-- Drop FK to teams if present so solo participant UUIDs can be persisted.
ALTER TABLE public.match_messages
    DROP CONSTRAINT IF EXISTS match_messages_team_id_fkey;

ALTER TABLE public.match_messages
    DROP CONSTRAINT IF EXISTS fk_match_messages_team_id;
