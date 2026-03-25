-- SECURITY DEFINER function to set tournament winner from bracket system
-- Runs as DB owner (postgres), so the trigger's current_user check allows it
CREATE OR REPLACE FUNCTION admin_set_tournament_winner(p_tournament_id UUID, p_winner_id UUID)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path = public
AS $$
BEGIN
  UPDATE tournaments
  SET winner_id = p_winner_id,
      status = 'completed'::tournament_status,
      end_date = NOW()
  WHERE id = p_tournament_id;
END;
$$;
