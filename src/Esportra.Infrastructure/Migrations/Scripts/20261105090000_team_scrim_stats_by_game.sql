-- team_scrim_stats(team_ids, game_slug): the same record as team_scrim_stats(team_ids), for one game only.
-- A team's VALORANT history must not appear on its listing for another game, so listing and offer
-- cards ask for the record of the listing's game. NULL game = every game (the old behaviour).
-- Same privacy rule: teams with scrim_history_public = FALSE get no row.

CREATE OR REPLACE FUNCTION public.team_scrim_stats(p_team_ids uuid[], p_game_slug text)
RETURNS TABLE (
    team_id        uuid,
    wins           integer,
    losses         integer,
    draws          integer,
    completed      integer,
    no_shows       integer,
    show_rate      numeric,
    last_played_at timestamptz,
    last5          text[],
    streak         integer
)
LANGUAGE sql
STABLE
AS $$
    WITH eligible AS (
        SELECT t.id
        FROM public.teams t
        WHERE t.id = ANY(p_team_ids)
          AND t.scrim_history_public = TRUE
    ),
    sides AS (
        SELECT ss.team1_id AS team_id, ss.id AS session_id, ss.status::text AS status,
               ss.updated_at, ss.cancelled_reason, TRUE AS is_t1,
               ss.team1_score AS my_score, ss.team2_score AS their_score,
               ss.team1_checked_in_at AS my_checkin
        FROM public.scrim_sessions ss
        WHERE ss.team1_id IN (SELECT id FROM eligible)
          AND ss.status IN ('completed', 'cancelled')
          AND (p_game_slug IS NULL OR lower(ss.game_slug) = lower(p_game_slug))
        UNION ALL
        SELECT ss.team2_id, ss.id, ss.status::text,
               ss.updated_at, ss.cancelled_reason, FALSE,
               ss.team2_score, ss.team1_score,
               ss.team2_checked_in_at
        FROM public.scrim_sessions ss
        WHERE ss.team2_id IN (SELECT id FROM eligible)
          AND ss.status IN ('completed', 'cancelled')
          AND (p_game_slug IS NULL OR lower(ss.game_slug) = lower(p_game_slug))
    ),
    graded AS (
        SELECT s.team_id, s.status, s.updated_at, s.cancelled_reason, s.my_checkin,
               CASE
                   WHEN s.status <> 'completed' THEN NULL
                   WHEN d.outcome = 'no_contest' THEN NULL
                   WHEN d.outcome = 'team1_wins' THEN CASE WHEN s.is_t1 THEN 'W' ELSE 'L' END
                   WHEN d.outcome = 'team2_wins' THEN CASE WHEN s.is_t1 THEN 'L' ELSE 'W' END
                   WHEN s.my_score IS NULL OR s.their_score IS NULL THEN NULL
                   WHEN s.my_score > s.their_score THEN 'W'
                   WHEN s.my_score < s.their_score THEN 'L'
                   ELSE 'D'
               END AS result
        FROM sides s
        LEFT JOIN LATERAL (
            SELECT sd.outcome
            FROM public.scrim_disputes sd
            WHERE sd.session_id = s.session_id
              AND sd.status = 'resolved'
              AND sd.outcome IS NOT NULL
            ORDER BY sd.resolved_at DESC NULLS LAST
            LIMIT 1
        ) d ON TRUE
    ),
    ranked AS (
        SELECT g.team_id, g.result,
               row_number() OVER (PARTITION BY g.team_id ORDER BY g.updated_at DESC) AS rn
        FROM graded g
        WHERE g.result IS NOT NULL
    ),
    form AS (
        SELECT r.team_id,
               array_agg(r.result ORDER BY r.rn) FILTER (WHERE r.rn <= 5) AS last5,
               (array_agg(r.result ORDER BY r.rn))[1] AS latest,
               COALESCE(
                   min(r.rn) FILTER (WHERE r.result IS DISTINCT FROM r.first_res) - 1,
                   count(*)
               ) AS run
        FROM (
            SELECT rk.*, first_value(rk.result) OVER (PARTITION BY rk.team_id ORDER BY rk.rn) AS first_res
            FROM ranked rk
        ) r
        GROUP BY r.team_id
    ),
    totals AS (
        SELECT g.team_id,
               count(*) FILTER (WHERE g.result = 'W')::int AS wins,
               count(*) FILTER (WHERE g.result = 'L')::int AS losses,
               count(*) FILTER (WHERE g.result = 'D')::int AS draws,
               count(*) FILTER (WHERE g.status = 'completed')::int AS completed,
               count(*) FILTER (WHERE g.status = 'cancelled'
                                  AND g.cancelled_reason = 'checkin_timeout'
                                  AND g.my_checkin IS NULL)::int AS no_shows,
               max(g.updated_at) FILTER (WHERE g.status = 'completed') AS last_played_at
        FROM graded g
        GROUP BY g.team_id
    )
    SELECT e.id,
           COALESCE(t.wins, 0), COALESCE(t.losses, 0), COALESCE(t.draws, 0),
           COALESCE(t.completed, 0), COALESCE(t.no_shows, 0),
           CASE WHEN COALESCE(t.completed, 0) + COALESCE(t.no_shows, 0) = 0 THEN NULL
                ELSE round(t.completed::numeric / (t.completed + t.no_shows), 4) END,
           t.last_played_at,
           COALESCE(f.last5, ARRAY[]::text[]),
           CASE f.latest
               WHEN 'W' THEN f.run::int
               WHEN 'L' THEN -f.run::int
               ELSE 0
           END
    FROM eligible e
    LEFT JOIN totals t ON t.team_id = e.id
    LEFT JOIN form f ON f.team_id = e.id;
$$;

-- Called by the API over its own connection only; never exposed through PostgREST.
REVOKE EXECUTE ON FUNCTION public.team_scrim_stats(uuid[], text) FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION public.team_scrim_stats(uuid[], text) FROM anon;
REVOKE EXECUTE ON FUNCTION public.team_scrim_stats(uuid[], text) FROM authenticated;
GRANT EXECUTE ON FUNCTION public.team_scrim_stats(uuid[], text) TO service_role;
