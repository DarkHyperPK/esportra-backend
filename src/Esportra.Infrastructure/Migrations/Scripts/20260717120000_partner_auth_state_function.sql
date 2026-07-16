CREATE OR REPLACE FUNCTION public.partner_auth_has_password(target_user_id UUID)
RETURNS BOOLEAN
LANGUAGE SQL
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, auth
AS $$
    SELECT COALESCE(LENGTH(users.encrypted_password), 0) > 0
    FROM auth.users AS users
    WHERE users.id = target_user_id;
$$;

REVOKE ALL ON FUNCTION public.partner_auth_has_password(UUID) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.partner_auth_has_password(UUID) TO service_role;
