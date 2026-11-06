-- PRE-FLIGHT: Run on production before deploying, confirm expected count before proceeding:
-- SELECT COUNT(*) FROM public.teams t
-- WHERE NOT EXISTS (
--     SELECT 1 FROM public.team_members tm
--     WHERE tm.team_id = t.id AND tm.user_id = t.owner_id AND tm.is_active = TRUE
-- );
-- Expected: small number of legacy accounts (teams created before the creator-captain insert was added)

INSERT INTO public.team_members (team_id, user_id, role, is_active)
SELECT t.id, t.owner_id, 'captain', TRUE
FROM public.teams t
WHERE t.owner_id IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM public.team_members tm
      WHERE tm.team_id = t.id AND tm.user_id = t.owner_id AND tm.is_active = TRUE
  )
ON CONFLICT DO NOTHING;
