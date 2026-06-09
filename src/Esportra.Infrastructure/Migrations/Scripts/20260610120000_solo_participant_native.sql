-- Solo participant native model: bracket slots use tournament_participants.id for solo entries.
-- Rewrites legacy solo adapter team IDs back to participant IDs and removes orphaned solo teams.

DO $$
DECLARE
    r RECORD;
BEGIN
    FOR r IN
        SELECT tp.id AS participant_id,
               tp.team_id AS adapter_team_id
        FROM public.tournament_participants tp
        JOIN public.teams t ON t.id = tp.team_id
        WHERE tp.participant_type = 'solo'
          AND tp.team_id IS NOT NULL
          AND COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) = 'solo'
    LOOP
        UPDATE public.brkt_matches
        SET team1_id = r.participant_id
        WHERE team1_id = r.adapter_team_id;

        UPDATE public.brkt_matches
        SET team2_id = r.participant_id
        WHERE team2_id = r.adapter_team_id;

        UPDATE public.brkt_matches
        SET winner_id = r.participant_id
        WHERE winner_id = r.adapter_team_id;

        UPDATE public.brkt_matches
        SET loser_id = r.participant_id
        WHERE loser_id = r.adapter_team_id;

        UPDATE public.match_result_reports
        SET reported_by_team_id = r.participant_id,
            winner_team_id = CASE WHEN winner_team_id = r.adapter_team_id THEN r.participant_id ELSE winner_team_id END
        WHERE reported_by_team_id = r.adapter_team_id
           OR winner_team_id = r.adapter_team_id;

        UPDATE public.brkt_match_games
        SET reported_by_team_id = r.participant_id,
            winner_id = CASE WHEN winner_id = r.adapter_team_id THEN r.participant_id ELSE winner_id END,
            loser_id = CASE WHEN loser_id = r.adapter_team_id THEN r.participant_id ELSE loser_id END
        WHERE reported_by_team_id = r.adapter_team_id
           OR winner_id = r.adapter_team_id
           OR loser_id = r.adapter_team_id;

        UPDATE public.match_checkins
        SET team_id = r.participant_id
        WHERE team_id = r.adapter_team_id;

        UPDATE public.match_disputes
        SET disputed_by_team_id = r.participant_id
        WHERE disputed_by_team_id = r.adapter_team_id;

        UPDATE public.tournament_disputes
        SET team_id = r.participant_id
        WHERE team_id = r.adapter_team_id;

        UPDATE public.tournament_participants
        SET team_id = NULL
        WHERE id = r.participant_id;
    END LOOP;
END;
$$;

-- Remove orphaned solo adapter teams (no remaining participant or bracket references).
DELETE FROM public.team_members tm
USING public.teams t
WHERE tm.team_id = t.id
  AND COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) = 'solo'
  AND NOT EXISTS (
      SELECT 1 FROM public.tournament_participants tp WHERE tp.team_id = t.id
  )
  AND NOT EXISTS (
      SELECT 1 FROM public.brkt_matches m
      WHERE m.team1_id = t.id OR m.team2_id = t.id OR m.winner_id = t.id OR m.loser_id = t.id
  );

DELETE FROM public.teams t
WHERE COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) = 'solo'
  AND NOT EXISTS (
      SELECT 1 FROM public.tournament_participants tp WHERE tp.team_id = t.id
  )
  AND NOT EXISTS (
      SELECT 1 FROM public.brkt_matches m
      WHERE m.team1_id = t.id OR m.team2_id = t.id OR m.winner_id = t.id OR m.loser_id = t.id
  )
  AND NOT EXISTS (
      SELECT 1 FROM public.team_members tm WHERE tm.team_id = t.id
  );
