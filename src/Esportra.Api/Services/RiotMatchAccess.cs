using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;

namespace Esportra.Api.Services;

/// <summary>
/// Decides who may read a Riot match through Esportra's API key. A match is shown when it was reported on
/// Esportra in a place the caller may see (tournament games are public; scrim games only to the two teams),
/// or when one of the caller's own linked Riot accounts played in it. Everything else would let any signed-in
/// user look up arbitrary players' games on our key.
/// </summary>
public static partial class RiotMatchAccess
{
    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex MatchIdPattern();

    public static bool IsWellFormedMatchId(string? matchId) =>
        matchId is not null && MatchIdPattern().IsMatch(matchId);

    /// <summary>True when the match id is attached to a game the caller is allowed to view.</summary>
    public static Task<bool> IsReportedAndVisibleAsync(IDbConnection conn, string matchId, Guid userId) =>
        conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM public.brkt_match_games WHERE riot_match_id = @matchId)
                OR EXISTS (SELECT 1 FROM public.match_result_reports WHERE riot_match_id = @matchId)
                OR EXISTS (
                    SELECT 1
                    FROM public.scrim_session_games g
                    JOIN public.scrim_sessions s ON s.id = g.session_id
                    JOIN public.team_members tm ON tm.team_id IN (s.team1_id, s.team2_id)
                    WHERE g.riot_match_id = @matchId
                      AND tm.user_id = @userId
                      AND tm.is_active = TRUE)
            """,
            new { matchId, userId });

    public static Task<IReadOnlyList<string>> LinkedPuuidsAsync(IDbConnection conn, Guid userId) =>
        conn.QueryAsync<string>(
                "SELECT puuid FROM public.riot_accounts WHERE user_id = @userId AND puuid IS NOT NULL",
                new { userId })
            .ContinueWith(t => (IReadOnlyList<string>)t.Result.ToList());

    /// <summary>True when any of the caller's linked accounts is a player in the fetched match.</summary>
    public static bool CallerPlayed(JsonElement matchRoot, IReadOnlyCollection<string> callerPuuids)
    {
        if (callerPuuids.Count == 0 || !matchRoot.TryGetProperty("players", out var players) ||
            players.ValueKind != JsonValueKind.Array) return false;
        foreach (var p in players.EnumerateArray())
            if (p.TryGetProperty("puuid", out var id) && id.GetString() is { } s && callerPuuids.Contains(s))
                return true;
        return false;
    }
}
