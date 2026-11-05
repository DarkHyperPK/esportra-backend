namespace Esportra.Api.Endpoints;

/// <summary>SQL fragments shared by the Squad Up (LFG swipe) endpoints.</summary>
internal static class LfgSwipeSql
{
    public const string GameSlug = "valorant";

    /// <summary>A session is live while it has not ended and its client has heartbeated recently.</summary>
    public static string Active(string alias) =>
        $"({alias}.ended_at IS NULL AND {alias}.last_heartbeat > now() - interval '3 minutes')";

    /// <summary>Same catalog version that LFG discovery uses.</summary>
    public const string ActiveVersionCte =
        "active_v AS (SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE LIMIT 1)";

    /// <summary>Riot ID is only shown when the player allows it (same rule as LFG discovery).</summary>
    public const string RiotGameName =
        "CASE WHEN COALESCE((p.privacy_settings->>'show_riot_account')::boolean, true) THEN ra.game_name END";

    public const string RiotTagLine =
        "CASE WHEN COALESCE((p.privacy_settings->>'show_riot_account')::boolean, true) THEN ra.tag_line END";

    public const string HsPct = "CASE WHEN pgs.headshot_data_complete THEN pgs.hs_pct END";

    public const string PopularAgents =
        """
        (CASE WHEN pgs.popular_agents IS NULL THEN NULL WHEN jsonb_typeof(pgs.popular_agents) = 'array' THEN (SELECT COALESCE(jsonb_agg(CASE WHEN item->>'headshot_data_complete' = 'true' THEN item ELSE item - 'hs_pct' END), '[]'::jsonb) FROM jsonb_array_elements(pgs.popular_agents) item) ELSE '[]'::jsonb END)::text
        """;

    /// <summary>Stats only count when they belong to the currently linked Riot account.</summary>
    public const string VerifiedStatsLink =
        "EXISTS (SELECT 1 FROM public.riot_accounts linked WHERE linked.user_id = pgs.user_id AND linked.puuid = pgs.provider_player_id)";

    /// <summary>
    /// Distinct roles of the three most-played agents. <paramref name="statsAlias"/> is a player_game_stats alias.
    /// popular_agents is ordered by games played; a non-array value yields no roles.
    /// </summary>
    public static string TopRoles(string statsAlias) =>
        $"""
        COALESCE((SELECT array_agg(DISTINCT gca.role_key)
                  FROM jsonb_array_elements(CASE WHEN jsonb_typeof({statsAlias}.popular_agents) = 'array'
                                                 THEN {statsAlias}.popular_agents ELSE '[]'::jsonb END)
                       WITH ORDINALITY AS a(val, ord)
                  JOIN public.game_catalog_agents gca
                    ON gca.game_slug = 'valorant' AND gca.agent_id = a.val->>'agent_id'
                  WHERE a.ord <= 3), ARRAY[]::text[])
        """;

    /// <summary>Caller session joined to a target session that is live and inside the caller's pool.</summary>
    public static string PoolTarget() =>
        $"""
        SELECT 1
        FROM public.lfg_swipe_sessions me
        JOIN public.lfg_swipe_sessions ts
          ON ts.game_slug = me.game_slug AND ts.user_id = @targetId AND ts.region = me.region
         AND ABS(ts.competitive_tier - me.competitive_tier) <= 8 AND {Active("ts")}
        JOIN public.profiles p ON p.id = ts.user_id AND COALESCE(p.is_suspended, false) = false
        WHERE me.user_id = @myId AND me.game_slug = 'valorant' AND {Active("me")}
        """;

    /// <summary>Unseen likes for @myId: likers whose session is live and whom @mySession has not acted on.</summary>
    public static string PendingLikes() =>
        $"""
        SELECT count(*)::int FROM public.lfg_swipe_actions a
        JOIN public.lfg_swipe_sessions s ON s.id = a.session_id AND {Active("s")}
        WHERE a.direction = 'like' AND a.target_user_id = @myId
          AND NOT EXISTS (SELECT 1 FROM public.lfg_swipe_actions mine
                          WHERE mine.session_id = @mySession AND mine.target_user_id = s.user_id)
        """;

    public static string Cards() =>
        $"""
        WITH {ActiveVersionCte},
        me AS (
            SELECT s.id, s.region, s.competitive_tier,
                   {TopRoles("mypgs")} AS roles
            FROM public.lfg_swipe_sessions s
            LEFT JOIN public.player_game_stats mypgs ON mypgs.user_id = s.user_id AND mypgs.game_slug = s.game_slug
            WHERE s.user_id = @myId AND s.game_slug = 'valorant' AND {Active("s")}
        )
        SELECT s.user_id, s.mic_on, s.slots_needed,
               p.username, p.avatar_seed, p.avatar_url, p.avatar_style,
               pgs.competitive_tier, pgs.competitive_tier_name, rt.icon_url AS competitive_tier_icon_url,
               pgs.win_rate, pgs.kd_ratio, {HsPct} AS hs_pct, COALESCE(pgs.matches_analyzed, 0) AS matches_analyzed,
               {PopularAgents} AS popular_agents,
               {RiotGameName} AS riot_game_name, {RiotTagLine} AS riot_tag_line,
               s.region, gcr.display_name AS region_display_name,
               syn.label AS synergy_label, syn.score AS synergy_score,
               EXISTS (SELECT 1 FROM public.lfg_swipe_actions liked
                       WHERE liked.session_id = s.id AND liked.target_user_id = @myId AND liked.direction = 'like') AS liked_you
        FROM me
        JOIN public.lfg_swipe_sessions s
          ON s.game_slug = 'valorant' AND s.user_id <> @myId AND s.region = me.region
         AND ABS(s.competitive_tier - me.competitive_tier) <= @tierRange AND {Active("s")}
        JOIN public.profiles p ON p.id = s.user_id AND COALESCE(p.is_suspended, false) = false
        JOIN public.player_game_stats pgs ON pgs.user_id = s.user_id AND pgs.game_slug = s.game_slug AND {VerifiedStatsLink}
        JOIN public.riot_accounts ra ON ra.user_id = s.user_id
        LEFT JOIN public.game_catalog_rank_tiers rt
          ON rt.game_slug = s.game_slug AND rt.sort_order = pgs.competitive_tier - 3
         AND rt.version_id = (SELECT id FROM active_v)
        LEFT JOIN public.game_catalog_regions gcr
          ON gcr.game_slug = s.game_slug AND gcr.region_key = s.region
         AND gcr.version_id = (SELECT id FROM active_v)
        CROSS JOIN LATERAL (SELECT {TopRoles("pgs")} AS roles) their
        CROSS JOIN LATERAL (
            SELECT CASE WHEN cardinality(me.roles) = 0 OR cardinality(their.roles) = 0 THEN 'unknown'
                        WHEN me.roles && their.roles THEN 'role_overlap' ELSE 'great_synergy' END AS label,
                   CASE WHEN cardinality(me.roles) = 0 OR cardinality(their.roles) = 0 THEN 0
                        WHEN me.roles && their.roles THEN 1 ELSE 2 END AS score) syn
        WHERE NOT EXISTS (SELECT 1 FROM public.lfg_swipe_actions seen
                          WHERE seen.session_id = me.id AND seen.target_user_id = s.user_id)
        ORDER BY liked_you DESC, synergy_score DESC, random()
        LIMIT @limit
        """;

    public static string Matches(string extraWhere = "") =>
        $"""
        WITH {ActiveVersionCte}
        SELECT m.id AS match_id, m.conversation_id, m.matched_at,
               o.id AS matched_user_id, o.username AS matched_username,
               o.avatar_url AS matched_avatar_url, o.avatar_style AS matched_avatar_style,
               pgs.competitive_tier, pgs.competitive_tier_name, rt.icon_url AS competitive_tier_icon_url,
               {RiotGameName} AS riot_game_name, {RiotTagLine} AS riot_tag_line,
               {PopularAgents} AS popular_agents, s.mic_on, s.slots_needed
        FROM public.lfg_swipe_matches m
        JOIN public.profiles o ON o.id = CASE WHEN m.user1_id = @myId THEN m.user2_id ELSE m.user1_id END
        LEFT JOIN public.profiles p ON p.id = o.id
        LEFT JOIN public.player_game_stats pgs ON pgs.user_id = o.id AND pgs.game_slug = m.game_slug AND {VerifiedStatsLink}
        LEFT JOIN public.riot_accounts ra ON ra.user_id = o.id
        LEFT JOIN public.game_catalog_rank_tiers rt
          ON rt.game_slug = m.game_slug AND rt.sort_order = pgs.competitive_tier - 3
         AND rt.version_id = (SELECT id FROM active_v)
        LEFT JOIN public.lfg_swipe_sessions s ON s.user_id = o.id AND s.game_slug = m.game_slug AND {Active("s")}
        WHERE (m.user1_id = @myId OR m.user2_id = @myId) {extraWhere}
          AND m.matched_at > now() - interval '24 hours'
        ORDER BY m.matched_at DESC
        LIMIT 20
        """;

    public const string Stats =
        """
        SELECT (SELECT count(*) FROM public.lfg_swipe_actions a WHERE a.session_id = s.id AND a.direction = 'like')::int AS swipes_right,
               (SELECT count(*) FROM public.lfg_swipe_actions a WHERE a.session_id = s.id AND a.direction = 'pass')::int AS swipes_left,
               (SELECT count(*) FROM public.lfg_swipe_matches m
                WHERE (m.user1_id = s.user_id OR m.user2_id = s.user_id)
                  AND m.game_slug = s.game_slug AND m.matched_at >= s.started_at)::int AS matches_count,
               GREATEST(0, EXTRACT(EPOCH FROM (COALESCE(s.ended_at, now()) - s.started_at)))::int AS duration_seconds
        FROM public.lfg_swipe_sessions s
        WHERE s.user_id = @myId AND s.game_slug = 'valorant'
        """;
}
