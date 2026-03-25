-- Replace fragile service_role session variable check with proper current_user check.
-- SECURITY DEFINER functions run as 'postgres', so this naturally allows them
-- without faking JWT claims. Also keeps admin profile check and actual service_role
-- connections working (Supabase internal calls set session_user to supabase_admin).

CREATE OR REPLACE FUNCTION public.block_organizer_sensitive_tournament_updates()
  RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
AS $function$
BEGIN
  -- Allow DB owner (covers SECURITY DEFINER functions like admin_set_tournament_winner)
  IF current_user = 'postgres' THEN
    RETURN NEW;
  END IF;

  -- Allow Supabase service_role connections (internal/admin API calls)
  IF current_setting('request.jwt.claim.role', true) = 'service_role' THEN
    RETURN NEW;
  END IF;

  -- Allow platform admins
  IF EXISTS (SELECT 1 FROM profiles WHERE id = auth.uid() AND is_admin = true) THEN
    RETURN NEW;
  END IF;

  -- Block organizers from updating sensitive columns
  IF NEW.is_featured IS DISTINCT FROM OLD.is_featured THEN
    RAISE EXCEPTION 'Only admins can change is_featured';
  END IF;

  IF NEW.status IS DISTINCT FROM OLD.status AND OLD.status::text = 'pending_approval' THEN
    RAISE EXCEPTION 'Only admins can approve tournaments';
  END IF;

  IF NEW.approved_by IS DISTINCT FROM OLD.approved_by THEN
    RAISE EXCEPTION 'Only admins can set approved_by';
  END IF;

  IF NEW.approved_at IS DISTINCT FROM OLD.approved_at THEN
    RAISE EXCEPTION 'Only admins can set approved_at';
  END IF;

  IF NEW.winner_id IS DISTINCT FROM OLD.winner_id THEN
    RAISE EXCEPTION 'Only admins can set winner_id';
  END IF;

  RETURN NEW;
END;
$function$;
