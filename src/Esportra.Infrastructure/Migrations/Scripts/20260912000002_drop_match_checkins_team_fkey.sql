-- The team_id / competitor_id columns on match-related tables are polymorphic:
-- they hold teams.id for team tournaments and tournament_participants.id for solo
-- tournaments. The solo-native migration (20260610120000) rewrote existing data
-- but missed dropping the teams-only FK constraints on three tables. Mirrors the
-- same fix applied to match_result_reports in 20260609170000 and match_messages
-- in 20260609160000.

-- match_checkins: solo participants cannot check in without this drop.
ALTER TABLE public.match_checkins
    DROP CONSTRAINT IF EXISTS match_checkins_team_id_fkey;

-- match_disputes: solo participants cannot file a match dispute without this drop.
ALTER TABLE public.match_disputes
    DROP CONSTRAINT IF EXISTS match_disputes_disputed_by_team_id_fkey;

-- tournament_disputes: same class of bug on the tournament-level dispute table.
ALTER TABLE public.tournament_disputes
    DROP CONSTRAINT IF EXISTS tournament_disputes_team_id_fkey;
