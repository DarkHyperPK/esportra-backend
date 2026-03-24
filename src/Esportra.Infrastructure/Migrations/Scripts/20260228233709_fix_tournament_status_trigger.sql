-- Fix block_organizer_sensitive_tournament_updates trigger
-- The trigger referenced 'pending_approval' in an enum comparison, but that value
-- was never added to the tournament_status enum. Any status change by an organizer
-- caused PostgreSQL to fail the implicit enum cast:
--   "invalid input value for enum tournament_status: 'pending_approval'"
-- Fix: cast OLD.status to text before comparing, so the literal string comparison
-- is safe regardless of what values exist in the enum.

CREATE OR REPLACE FUNCTION public.block_organizer_sensitive_tournament_updates()
  RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
AS $function$
BEGIN
  -- Allow service_role and admins to update anything
  IF current_setting('request.jwt.claim.role', true) = 'service_role' THEN
    RETURN NEW;
  END IF;

  IF EXISTS (SELECT 1 FROM profiles WHERE id = auth.uid() AND is_admin = true) THEN
    RETURN NEW;
  END IF;

  -- Block organizers from updating these sensitive columns
  IF NEW.is_featured IS DISTINCT FROM OLD.is_featured THEN
    RAISE EXCEPTION 'Only admins can change is_featured';
  END IF;

  -- Cast to text before comparing so this is safe even if the enum value
  -- does not currently exist (avoids implicit cast failure).
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
