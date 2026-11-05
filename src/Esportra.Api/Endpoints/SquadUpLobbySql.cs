namespace Esportra.Api.Endpoints;

/// <summary>SQL for Squad Up lobbies. Compatibility reuses <see cref="LfgSwipeSql.Accepts"/> so Duo and Lobby never drift.</summary>
internal static class SquadUpLobbySql
{
    /// <summary>An open lobby is listed while its host is present (tab within 3 min, active within 10 min).</summary>
    public static string Open(string alias) =>
        $"({alias}.status = 'open' AND {alias}.last_heartbeat > now() - interval '3 minutes' AND {alias}.last_activity_at > now() - interval '10 minutes')";

    /// <summary>A row describing the caller, shaped like a session so the shared compatibility rules apply.</summary>
    public const string MeCte =
        """
        me AS (SELECT @game::text AS game_slug, @mode::text AS mode, @region::text AS region, @area::text AS area,
                      @tier::int AS competitive_tier, @minTier::int AS min_tier, @maxTier::int AS max_tier)
        """;

    public const string AcceptedCount =
        "(SELECT count(*) FROM public.squad_up_lobby_members am WHERE am.lobby_id = l.id AND am.status = 'accepted')::int";

    /// <summary>Lobby card columns. Needs @myId. The chat id is only returned to the host and accepted members.</summary>
    public static readonly string Select =
        $"""
        SELECT l.id, l.host_id, hp.username AS host_username, hp.avatar_url AS host_avatar_url, hp.avatar_seed AS host_avatar_seed,
               hp.avatar_style AS host_avatar_style, l.competitive_tier AS host_tier, pgs.competitive_tier_name AS host_tier_name,
               l.title, l.mode, l.region, l.country_code, l.area, l.min_tier, l.max_tier, l.open_slots,
               {AcceptedCount} AS accepted_count, l.wanted_roles, l.mic_required, l.status, l.created_at,
               (SELECT mm.status FROM public.squad_up_lobby_members mm WHERE mm.lobby_id = l.id AND mm.user_id = @myId) AS my_status,
               CASE WHEN l.host_id = @myId OR EXISTS (SELECT 1 FROM public.squad_up_lobby_members cm
                                                       WHERE cm.lobby_id = l.id AND cm.user_id = @myId AND cm.status = 'accepted')
                    THEN l.conversation_id END AS conversation_id,
               COALESCE((SELECT jsonb_agg(jsonb_build_object('user_id', m.user_id, 'username', mp.username, 'role_key', m.role_key,
                                                             'avatar_url', mp.avatar_url, 'avatar_seed', mp.avatar_seed, 'avatar_style', mp.avatar_style)
                                          ORDER BY m.decided_at)
                         FROM public.squad_up_lobby_members m JOIN public.profiles mp ON mp.id = m.user_id
                         WHERE m.lobby_id = l.id AND m.status = 'accepted'), '[]'::jsonb)::text AS members_json
        FROM public.squad_up_lobbies l
        JOIN public.profiles hp ON hp.id = l.host_id
        LEFT JOIN public.player_game_stats pgs ON pgs.user_id = l.host_id AND pgs.game_slug = l.game_slug AND {LfgSwipeSql.VerifiedStatsLink}
        """;

    /// <summary>Open lobbies the caller could join and would want to see (compatible both ways, not blocked, not full).</summary>
    public static readonly string Board =
        $"""
        WITH {MeCte}
        {Select}
        CROSS JOIN me
        WHERE {Open("l")} AND l.host_id <> @myId
          AND COALESCE(hp.is_suspended, false) = false
          AND {LfgSwipeSql.Compatible("l", "me")}
          AND {LfgSwipeSql.NotBlocked("@myId", "l.host_id")}
          AND {AcceptedCount} < l.open_slots
        ORDER BY (l.country_code = @country) DESC NULLS LAST, l.created_at DESC
        LIMIT 30
        """;

    /// <summary>People in a lobby (host view): requests and accepted players, with what the host needs to decide.</summary>
    public static readonly string Members =
        $"""
        SELECT m.user_id, p.username, p.avatar_url, p.avatar_seed, p.avatar_style, m.role_key, m.status, m.created_at,
               pgs.competitive_tier, pgs.competitive_tier_name,
               {LfgSwipeSql.RiotGameName} AS riot_game_name, {LfgSwipeSql.RiotTagLine} AS riot_tag_line
        FROM public.squad_up_lobby_members m
        JOIN public.profiles p ON p.id = m.user_id
        LEFT JOIN public.riot_accounts ra ON ra.user_id = m.user_id
        LEFT JOIN public.player_game_stats pgs ON pgs.user_id = m.user_id AND pgs.game_slug = 'valorant' AND {LfgSwipeSql.VerifiedStatsLink}
        WHERE m.lobby_id = @lobbyId AND m.status IN ('requested', 'accepted')
        ORDER BY (m.status = 'accepted') DESC, m.created_at
        """;

    /// <summary>Open lobbies whose host went quiet are closed when anyone looks (they are already invisible on the board).</summary>
    public static readonly string CloseStale =
        """
        UPDATE public.squad_up_lobbies SET status = 'closed', closed_at = now()
        WHERE status = 'open' AND (last_heartbeat <= now() - interval '3 minutes' OR last_activity_at <= now() - interval '10 minutes')
          AND host_id = @hostId
        """;
}
