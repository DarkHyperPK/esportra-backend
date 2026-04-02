-- Virtual Teams: auto-create a "team" row for every solo tournament participant
-- so that brkt_matches.team1_id / team2_id always references teams(id).

-- 1. Add is_solo flag to distinguish virtual teams from real ones
ALTER TABLE public.teams ADD COLUMN IF NOT EXISTS is_solo BOOLEAN NOT NULL DEFAULT false;

-- 2. Back-fill: create virtual teams for existing solo participants that have no team_id
DO $$
DECLARE
    r  RECORD;
    vt_id  UUID;
    vt_tag TEXT;
BEGIN
    FOR r IN
        SELECT tp.id  AS participant_id,
               tp.user_id,
               tp.tournament_id,
               COALESCE(p.username, 'Player') AS username,
               p.avatar_url,
               t.game
        FROM   public.tournament_participants tp
        JOIN   public.tournaments t ON t.id = tp.tournament_id
        JOIN   public.profiles    p ON p.id = tp.user_id
        WHERE  tp.participant_type = 'solo'
          AND  tp.team_id IS NULL
    LOOP
        vt_id  := gen_random_uuid();
        vt_tag := 'solo-' || replace(vt_id::text, '-', '');

        -- Create virtual team
        INSERT INTO public.teams
            (id, name, tag, game, owner_id, is_solo, max_members, logo_url)
        VALUES
            (vt_id, r.username, vt_tag, r.game, r.user_id, true, 1, r.avatar_url);

        -- Link participant to virtual team
        UPDATE public.tournament_participants
        SET    team_id = vt_id
        WHERE  id = r.participant_id;

        -- Patch any bracket matches that stored participant UUID as team ref
        UPDATE public.brkt_matches SET team1_id  = vt_id WHERE team1_id  = r.participant_id;
        UPDATE public.brkt_matches SET team2_id  = vt_id WHERE team2_id  = r.participant_id;
        UPDATE public.brkt_matches SET winner_id = vt_id WHERE winner_id = r.participant_id;
        UPDATE public.brkt_matches SET loser_id  = vt_id WHERE loser_id  = r.participant_id;

        -- Add player as captain in team_members
        INSERT INTO public.team_members (team_id, user_id, role, is_active)
        VALUES (vt_id, r.user_id, 'captain', true)
        ON CONFLICT DO NOTHING;
    END LOOP;
END;
$$;
