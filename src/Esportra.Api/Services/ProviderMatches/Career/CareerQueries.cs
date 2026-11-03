using System.Data;
using Dapper;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Services.ProviderMatches.Career;

/// <summary>
/// What a viewer may see of a player's stats. Private by default: the player, staff of a team they play for (that
/// team's games only), and staff admins. A player who opts in makes the summary public; the game-by-game list then still
/// shows only tournament games (already public) plus the viewer's own team's games.
/// </summary>
public sealed record PlayerAccess(string Level, Guid[]? SummaryTeamIds, bool FullHistory, Guid[] StaffTeamIds)
{
    public static readonly PlayerAccess None = new("none", [], false, []);
}

public sealed record TeamAccess(string Level, bool IncludeScrims);

public static class CareerQueries
{
    public const int SummaryRowCap = 2000;
    private const string StaffRoles = "('captain', 'manager', 'coach', 'owner')";
    private const string CountedStatuses = "('verified', 'partially_verified')";

    public static async Task<PlayerAccess> ResolvePlayerAccessAsync(IDbConnection conn, UserContext viewer, Guid targetUserId, string game)
    {
        if (viewer.UserIdGuid == targetUserId || viewer.AdminRoles.Length > 0)
            return new PlayerAccess(viewer.UserIdGuid == targetUserId ? "self" : "admin", null, true, []);

        var staffTeams = (await conn.QueryAsync<Guid>(
            $"""
            SELECT v.team_id FROM public.team_members v
            JOIN public.team_members t ON t.team_id = v.team_id AND t.user_id = @target AND t.is_active = TRUE
            WHERE v.user_id = @viewer AND v.is_active = TRUE AND v.role::text IN {StaffRoles}
            """, new { viewer = viewer.UserIdGuid, target = targetUserId })).ToArray();

        var isPublic = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM public.player_analytics_settings WHERE user_id = @targetUserId AND game_slug = @game AND visibility = 'public')",
            new { targetUserId, game });

        if (isPublic) return new PlayerAccess("public", null, false, staffTeams);
        if (staffTeams.Length > 0) return new PlayerAccess("team_staff", staffTeams, true, staffTeams);
        return PlayerAccess.None;
    }

    public static async Task<TeamAccess> ResolveTeamAccessAsync(IDbConnection conn, UserContext viewer, Guid teamId)
    {
        var member = viewer.AdminRoles.Length > 0 || await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM public.team_members WHERE team_id = @teamId AND user_id = @userId AND is_active = TRUE)",
            new { teamId, userId = viewer.UserIdGuid });
        if (member) return new TeamAccess("member", true);
        var scrimsPublic = await conn.ExecuteScalarAsync<bool>(
            "SELECT COALESCE((SELECT scrim_history_public FROM public.teams WHERE id = @teamId), FALSE)", new { teamId });
        return new TeamAccess("public", scrimsPublic);
    }

    /// <summary>
    /// A player's verified games, newest first. <paramref name="scopeTeamIds"/> limits to games for those teams (null = all);
    /// <paramref name="fullHistory"/> false keeps only tournament games and games for <paramref name="staffTeamIds"/>.
    /// </summary>
    public static async Task<List<PlayerMatchRow>> PlayerRowsAsync(
        IDbConnection conn, Guid userId, Guid[]? scopeTeamIds, bool fullHistory, Guid[] staffTeamIds, DateTime? before, int limit) =>
        (await conn.QueryAsync<PlayerMatchRow>(
            $"""
            SELECT m.id AS MatchId, m.started_at AS StartedAt, a.context_type AS ContextType, a.context_id AS ContextId,
                   g.session_id AS ScrimSessionId, a.tournament_match_id AS TournamentMatchId, a.game_number AS GameNumber,
                   m.map_provider_id AS MapId, s.team_id AS TeamId, t.name AS TeamName,
                   CASE WHEN s.team_slot = 1 THEN a.team2_id ELSE a.team1_id END AS OpponentId, ot.name AS OpponentName,
                   mine.rounds_won AS RoundsWon, theirs.rounds_won AS RoundsLost,
                   p.character_id AS CharacterId, p.score AS Score, p.rounds_played AS RoundsPlayed, p.kills AS Kills,
                   p.deaths AS Deaths, p.assists AS Assists, p.damage_dealt AS DamageDealt, p.headshots AS Headshots,
                   p.bodyshots AS Bodyshots, p.legshots AS Legshots, p.first_kills AS FirstKills, p.first_deaths AS FirstDeaths,
                   p.kills_2k AS Kills2K, p.kills_3k AS Kills3K, p.kills_4k AS Kills4K, p.kills_5k AS Kills5K,
                   p.plants AS Plants, p.defuses AS Defuses
            FROM public.provider_match_roster_snapshots s
            JOIN public.provider_match_associations a ON a.id = s.association_id
            JOIN public.provider_matches m ON m.id = a.match_id
            JOIN public.valorant_match_players p ON p.match_id = m.id AND p.puuid = s.puuid
            LEFT JOIN public.valorant_match_teams mine ON mine.match_id = m.id AND mine.team_id = p.riot_side
            LEFT JOIN public.valorant_match_teams theirs ON theirs.match_id = m.id AND theirs.team_id <> p.riot_side
            LEFT JOIN public.scrim_session_games g ON a.context_type = 'scrim_game' AND g.id = a.context_id
            LEFT JOIN public.teams t ON t.id = s.team_id
            LEFT JOIN public.teams ot ON ot.id = (CASE WHEN s.team_slot = 1 THEN a.team2_id ELSE a.team1_id END)
            WHERE s.user_id = @userId AND s.played = TRUE AND NOT p.is_observer
              AND a.verification_status IN {CountedStatuses} AND m.data_status = 'retrieved' AND m.is_completed = TRUE
              AND (@scopeTeamIds::uuid[] IS NULL OR s.team_id = ANY(@scopeTeamIds::uuid[]))
              AND (@fullHistory OR a.context_type = 'tournament_game' OR s.team_id = ANY(@staffTeamIds::uuid[]))
              AND (@before::timestamptz IS NULL OR m.started_at < @before::timestamptz)
            ORDER BY m.started_at DESC NULLS LAST, m.id
            LIMIT @limit
            """, new { userId, scopeTeamIds, fullHistory, staffTeamIds, before, limit })).ToList();

    /// <summary>A team's verified games (the figures are sums over the team's own Riot side), newest first.</summary>
    public static async Task<List<TeamMatchRow>> TeamRowsAsync(
        IDbConnection conn, Guid teamId, bool includeScrims, DateTime? before, int limit) =>
        (await conn.QueryAsync<TeamMatchRow>(
            $"""
            WITH side AS (
                SELECT a.*, CASE WHEN a.team1_id = @teamId THEN a.team1_riot_side ELSE a.team2_riot_side END AS my_side,
                       CASE WHEN a.team1_id = @teamId THEN a.team2_id ELSE a.team1_id END AS opp_id
                FROM public.provider_match_associations a
                WHERE (a.team1_id = @teamId OR a.team2_id = @teamId) AND a.verification_status IN {CountedStatuses}
                  AND (@includeScrims OR a.context_type = 'tournament_game')
            )
            SELECT m.id AS MatchId, m.started_at AS StartedAt, x.context_type AS ContextType, x.context_id AS ContextId,
                   g.session_id AS ScrimSessionId, x.tournament_match_id AS TournamentMatchId, x.game_number AS GameNumber,
                   m.map_provider_id AS MapId, x.opp_id AS OpponentId, ot.name AS OpponentName,
                   mine.rounds_won AS RoundsWon, theirs.rounds_won AS RoundsLost, COALESCE(mine.rounds_played, 0) AS RoundsPlayed,
                   COALESCE(sums.kills, 0) AS Kills, COALESCE(sums.deaths, 0) AS Deaths, COALESCE(sums.assists, 0) AS Assists,
                   COALESCE(sums.damage, 0) AS DamageDealt, COALESCE(sums.first_kills, 0) AS FirstKills,
                   COALESCE(sums.first_deaths, 0) AS FirstDeaths
            FROM side x
            JOIN public.provider_matches m ON m.id = x.match_id AND m.data_status = 'retrieved' AND m.is_completed = TRUE
            JOIN public.valorant_match_teams mine ON mine.match_id = m.id AND mine.team_id = x.my_side
            LEFT JOIN public.valorant_match_teams theirs ON theirs.match_id = m.id AND theirs.team_id <> x.my_side
            LEFT JOIN LATERAL (
                SELECT SUM(p.kills)::int AS kills, SUM(p.deaths)::int AS deaths, SUM(p.assists)::int AS assists,
                       SUM(p.damage_dealt)::int AS damage, SUM(p.first_kills)::int AS first_kills, SUM(p.first_deaths)::int AS first_deaths
                FROM public.valorant_match_players p
                WHERE p.match_id = m.id AND p.riot_side = x.my_side AND NOT p.is_observer
            ) sums ON TRUE
            LEFT JOIN public.scrim_session_games g ON x.context_type = 'scrim_game' AND g.id = x.context_id
            LEFT JOIN public.teams ot ON ot.id = x.opp_id
            WHERE (@before::timestamptz IS NULL OR m.started_at < @before::timestamptz)
            ORDER BY m.started_at DESC NULLS LAST, m.id
            LIMIT @limit
            """, new { teamId, includeScrims, before, limit })).ToList();

    /// <summary>Who has played verified games for a team (members only: it names people).</summary>
    public static async Task<List<TeamAppearance>> TeamAppearancesAsync(IDbConnection conn, Guid teamId) =>
        (await conn.QueryAsync<TeamAppearance>(
            $"""
            SELECT s.user_id AS UserId, pr.username AS Username, COUNT(*)::int AS Matches, MAX(m.started_at) AS LastPlayed
            FROM public.provider_match_roster_snapshots s
            JOIN public.provider_match_associations a ON a.id = s.association_id
            JOIN public.provider_matches m ON m.id = a.match_id
            LEFT JOIN public.profiles pr ON pr.id = s.user_id
            WHERE s.team_id = @teamId AND s.user_id IS NOT NULL AND s.played = TRUE
              AND a.verification_status IN {CountedStatuses} AND m.data_status = 'retrieved'
            GROUP BY s.user_id, pr.username
            ORDER BY COUNT(*) DESC, MAX(m.started_at) DESC
            LIMIT 50
            """, new { teamId })).ToList();
}

public sealed record TeamAppearance(Guid UserId, string? Username, int Matches, DateTime? LastPlayed);
