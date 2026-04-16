-- ============================================================================
-- Migration: Drop all Faceit support
-- ============================================================================

-- Drop faceit_accounts table if it exists
DROP TABLE IF EXISTS public.faceit_accounts CASCADE;

-- Drop faceit_nickname column from profiles if it exists
ALTER TABLE public.profiles DROP COLUMN IF EXISTS faceit_nickname;

-- Drop and recreate get_roster_members without faceit_nickname in return type
DROP FUNCTION IF EXISTS public.get_roster_members(UUID);

CREATE FUNCTION public.get_roster_members(p_team_id UUID)
RETURNS TABLE (
    user_id UUID,
    role TEXT,
    username TEXT,
    avatar_url TEXT,
    riot_tag TEXT,
    card_image_url TEXT
) LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
    RETURN QUERY
    SELECT
        tm.user_id,
        tm.role,
        p.username,
        p.avatar_url,
        p.riot_tag,
        p.card_image_url
    FROM team_members tm
    JOIN profiles p ON p.id = tm.user_id
    WHERE tm.team_id = p_team_id AND tm.is_active = true
    ORDER BY tm.display_order, tm.role, p.username;
END;
$$;
