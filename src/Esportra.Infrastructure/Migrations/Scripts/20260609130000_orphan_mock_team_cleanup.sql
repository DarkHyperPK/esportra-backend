-- Remove mock backing teams that no longer have a mock tournament participant.

DELETE FROM public.teams t
WHERE COALESCE(t.team_kind, CASE WHEN t.tag LIKE 'mock-%' THEN 'mock' ELSE 'team' END) = 'mock'
  AND NOT EXISTS (
      SELECT 1
      FROM public.tournament_participants tp
      WHERE tp.team_id = t.id
        AND COALESCE(tp.is_mock, false) = true
  );
