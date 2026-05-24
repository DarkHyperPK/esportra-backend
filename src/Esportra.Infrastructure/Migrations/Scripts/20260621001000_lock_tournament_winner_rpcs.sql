-- Winner mutation helpers are backend/admin infrastructure, not client RPCs.
-- Keep direct browser clients from invoking SECURITY DEFINER helpers through
-- PostgREST while preserving backend DB-owner and service_role execution.

REVOKE EXECUTE ON FUNCTION public.admin_set_tournament_winner(UUID, UUID) FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION public.admin_set_tournament_winner(UUID, UUID) FROM anon;
REVOKE EXECUTE ON FUNCTION public.admin_set_tournament_winner(UUID, UUID) FROM authenticated;
GRANT EXECUTE ON FUNCTION public.admin_set_tournament_winner(UUID, UUID) TO service_role;

REVOKE EXECUTE ON FUNCTION public.admin_clear_tournament_winner(UUID, BOOLEAN) FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION public.admin_clear_tournament_winner(UUID, BOOLEAN) FROM anon;
REVOKE EXECUTE ON FUNCTION public.admin_clear_tournament_winner(UUID, BOOLEAN) FROM authenticated;
GRANT EXECUTE ON FUNCTION public.admin_clear_tournament_winner(UUID, BOOLEAN) TO service_role;
