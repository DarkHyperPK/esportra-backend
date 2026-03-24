-- SECURITY DEFINER RPC that notifies all moderators + ops_admins.
-- Called from the frontend after a dispute is filed that goes to admin
-- (cheating, unsportsmanlike, other, general_support).
-- Running as SECURITY DEFINER so the frontend doesn't need SELECT on admin tables.

CREATE OR REPLACE FUNCTION public.notify_admins_of_dispute(
  p_dispute_id  UUID,
  p_type        TEXT,
  p_title       TEXT,
  p_message     TEXT,
  p_link        TEXT
)
RETURNS VOID
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
  v_admin_ids UUID[];
BEGIN
  -- Collect moderators / ops_admins from profiles.admin_roles
  SELECT ARRAY_AGG(DISTINCT p.id)
  INTO v_admin_ids
  FROM public.profiles p
  WHERE 'moderator' = ANY(p.admin_roles)
     OR 'ops_admin'  = ANY(p.admin_roles);

  -- Also include users in admin_user_roles with those role names
  SELECT ARRAY_AGG(DISTINCT aur.user_id) || COALESCE(v_admin_ids, '{}')
  INTO v_admin_ids
  FROM public.admin_user_roles aur
  JOIN public.admin_roles ar ON ar.id = aur.role_id
  WHERE lower(ar.name) = ANY(ARRAY['moderator', 'ops_admin']);

  -- Deduplicate and insert one notification per admin
  INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
  SELECT DISTINCT uid, p_type, p_title, p_message, p_link,
         jsonb_build_object('dispute_id', p_dispute_id),
         false
  FROM UNNEST(v_admin_ids) AS uid
  WHERE uid IS NOT NULL;
END;
$$;

GRANT EXECUTE ON FUNCTION public.notify_admins_of_dispute TO authenticated;
