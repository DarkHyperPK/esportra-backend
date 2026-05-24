-- SECURITY DEFINER helper for server-side tournament resets.
-- Organizer-facing API flows may need to clear a tournament winner when a bracket
-- is reset, deleted, or mock-only simulation data is cleared. The tournament
-- trigger correctly blocks direct winner_id changes by normal callers, so this
-- audited server helper performs the sensitive update as the DB owner.

CREATE OR REPLACE FUNCTION public.admin_clear_tournament_winner(
    p_tournament_id UUID,
    p_reopen_completed BOOLEAN DEFAULT FALSE
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
    UPDATE public.tournaments
       SET winner_id = NULL,
           status = CASE
               WHEN p_reopen_completed AND status::text = 'completed'
                   THEN 'open'::public.tournament_status
               ELSE status
           END,
           updated_at = NOW()
     WHERE id = p_tournament_id
       AND winner_id IS NOT NULL;
END;
$$;

GRANT EXECUTE ON FUNCTION public.admin_clear_tournament_winner(UUID, BOOLEAN) TO authenticated;
GRANT EXECUTE ON FUNCTION public.admin_clear_tournament_winner(UUID, BOOLEAN) TO service_role;
