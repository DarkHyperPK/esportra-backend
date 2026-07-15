-- Replace legacy roster cap trigger (team_size-only, single bucket) with catalog-aware
-- player/coach limits aligned to game_catalog_game_modes.

CREATE OR REPLACE FUNCTION public.resolve_roster_capacity_limits(
    p_roster_id uuid,
    OUT max_players int,
    OUT max_coaches int,
    OUT allows_coaches boolean
)
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path TO public
AS $$
DECLARE
    v_game text;
    v_format text;
    v_team_size int;
    v_game_slug text;
    v_mode_max_roster int;
    v_mode_team_size int;
    v_mode_max_coaches int;
    v_mode_allows_coaches boolean;
BEGIN
    SELECT r.game, r.format, r.team_size
    INTO v_game, v_format, v_team_size
    FROM public.team_rosters r
    WHERE r.id = p_roster_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Roster not found';
    END IF;

    SELECT a.game_slug
    INTO v_game_slug
    FROM public.game_catalog_versions v
    JOIN public.game_catalog_game_aliases a ON a.version_id = v.id
    WHERE v.is_active = TRUE
      AND v.status = 'active'
      AND LOWER(a.alias) = LOWER(COALESCE(v_game, ''))
    LIMIT 1;

    IF v_game_slug IS NOT NULL THEN
        IF v_format IS NOT NULL AND BTRIM(v_format) <> '' THEN
            SELECT m.max_roster_size,
                   m.team_size,
                   COALESCE(m.max_coaches, 2),
                   COALESCE(m.allows_coaches, TRUE)
            INTO v_mode_max_roster, v_mode_team_size, v_mode_max_coaches, v_mode_allows_coaches
            FROM public.game_catalog_versions v
            JOIN public.game_catalog_game_modes m ON m.version_id = v.id
            WHERE v.is_active = TRUE
              AND v.status = 'active'
              AND m.game_slug = v_game_slug
              AND (
                  LOWER(m.mode_key) = LOWER(v_format)
                  OR EXISTS (
                      SELECT 1
                      FROM unnest(COALESCE(m.aliases, ARRAY[]::text[])) AS alias(value)
                      WHERE LOWER(alias.value) = LOWER(v_format)
                  )
              )
            ORDER BY m.mode_key ASC
            LIMIT 1;

            IF FOUND THEN
                max_players := COALESCE(v_mode_max_roster, v_mode_team_size);
                max_coaches := v_mode_max_coaches;
                allows_coaches := v_mode_allows_coaches;
                RETURN;
            END IF;
        END IF;

        IF v_mode_team_size IS NULL THEN
            SELECT m.max_roster_size,
                   m.team_size,
                   COALESCE(m.max_coaches, 2),
                   COALESCE(m.allows_coaches, TRUE)
            INTO v_mode_max_roster, v_mode_team_size, v_mode_max_coaches, v_mode_allows_coaches
            FROM public.game_catalog_versions v
            JOIN public.game_catalog_game_modes m ON m.version_id = v.id
            WHERE v.is_active = TRUE
              AND v.status = 'active'
              AND m.game_slug = v_game_slug
              AND m.team_size = v_team_size
            ORDER BY m.mode_key ASC
            LIMIT 1;
        END IF;

        IF v_mode_team_size IS NOT NULL THEN
            max_players := COALESCE(v_mode_max_roster, v_mode_team_size);
            max_coaches := v_mode_max_coaches;
            allows_coaches := v_mode_allows_coaches;
            RETURN;
        END IF;
    END IF;

    max_players := v_team_size;
    max_coaches := 2;
    allows_coaches := TRUE;
END;
$$;

CREATE OR REPLACE FUNCTION public.enforce_roster_member_limits()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path TO public
AS $$
DECLARE
    v_limits record;
    v_role text;
    v_player_count int;
    v_coach_count int;
BEGIN
    IF TG_OP = 'INSERT' AND EXISTS (
        SELECT 1
        FROM public.team_roster_members trm
        WHERE trm.roster_id = NEW.roster_id
          AND trm.user_id = NEW.user_id
    ) THEN
        RETURN NEW;
    END IF;

    IF TG_OP = 'UPDATE'
       AND OLD.roster_role IS NOT DISTINCT FROM NEW.roster_role
       AND OLD.is_starter IS NOT DISTINCT FROM NEW.is_starter THEN
        RETURN NEW;
    END IF;

    v_role := COALESCE(
        NEW.roster_role::text,
        CASE WHEN COALESCE(NEW.is_starter, TRUE) THEN 'starter' ELSE 'substitute' END
    );

    SELECT *
    INTO v_limits
    FROM public.resolve_roster_capacity_limits(NEW.roster_id);

    IF v_role = 'coach' THEN
        IF NOT v_limits.allows_coaches THEN
            RAISE EXCEPTION 'This game mode does not allow coaches.';
        END IF;

        SELECT COUNT(*)
        INTO v_coach_count
        FROM public.team_roster_members trm
        WHERE trm.roster_id = NEW.roster_id
          AND COALESCE(
              trm.roster_role::text,
              CASE WHEN COALESCE(trm.is_starter, TRUE) THEN 'starter' ELSE 'substitute' END
          ) = 'coach'
          AND (TG_OP = 'INSERT' OR trm.user_id <> NEW.user_id);

        IF v_coach_count >= v_limits.max_coaches THEN
            RAISE EXCEPTION 'Coach limit reached (max %).', v_limits.max_coaches;
        END IF;

        RETURN NEW;
    END IF;

    SELECT COUNT(*)
    INTO v_player_count
    FROM public.team_roster_members trm
    WHERE trm.roster_id = NEW.roster_id
      AND COALESCE(
          trm.roster_role::text,
          CASE WHEN COALESCE(trm.is_starter, TRUE) THEN 'starter' ELSE 'substitute' END
      ) IN ('starter', 'substitute')
      AND (TG_OP = 'INSERT' OR trm.user_id <> NEW.user_id);

    IF v_player_count >= v_limits.max_players THEN
        RAISE EXCEPTION 'Player limit reached (max %).', v_limits.max_players;
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_enforce_roster_member_limits ON public.team_roster_members;

CREATE TRIGGER trg_enforce_roster_member_limits
    BEFORE INSERT ON public.team_roster_members
    FOR EACH ROW
    EXECUTE FUNCTION public.enforce_roster_member_limits();

DROP TRIGGER IF EXISTS trg_enforce_roster_member_limits_update ON public.team_roster_members;

CREATE TRIGGER trg_enforce_roster_member_limits_update
    BEFORE UPDATE OF roster_role, is_starter ON public.team_roster_members
    FOR EACH ROW
    EXECUTE FUNCTION public.enforce_roster_member_limits();

GRANT EXECUTE ON FUNCTION public.resolve_roster_capacity_limits(uuid) TO service_role;
GRANT EXECUTE ON FUNCTION public.enforce_roster_member_limits() TO service_role;
