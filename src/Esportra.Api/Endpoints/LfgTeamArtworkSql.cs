namespace Esportra.Api.Endpoints;

/// <summary>Only the current captain's linked Valorant account supplies team artwork.</summary>
internal static class LfgTeamArtworkSql
{
    // Older teams may have a captain member without teams.captain_id. Require a
    // single active captain in that case; never substitute the owner/representative.
    public const string CaptainValorantPlayerCard = """
        (SELECT captain_stats.player_card_id
         FROM public.player_game_stats captain_stats
         JOIN public.riot_accounts captain_riot ON captain_riot.user_id = captain_stats.user_id
             AND captain_riot.puuid = captain_stats.provider_player_id
         WHERE captain_stats.game_slug = 'valorant'
           AND captain_stats.user_id = COALESCE(t.captain_id,
               (SELECT (array_agg(captain_member.user_id))[1]
                FROM public.team_members captain_member
                WHERE captain_member.team_id = t.id AND captain_member.role = 'captain'
                  AND captain_member.is_active = TRUE HAVING count(*) = 1)))
        """;
}
