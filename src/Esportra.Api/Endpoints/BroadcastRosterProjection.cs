using System.Data;
using Dapper;
using Esportra.Core.Broadcast;

namespace Esportra.Api.Endpoints;

public static partial class BroadcastShowEndpoints
{
    private static async Task<Dictionary<Guid, BroadcastRosterName[]>> ReadRegisteredRosters(IDbConnection conn, Guid? tournamentId, IEnumerable<Guid?> teamIds)
    {
        var ids = teamIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        if (tournamentId is null || ids.Length == 0) return [];
        var rows = await conn.QueryAsync<RegisteredRosterRow>("""
            SELECT DISTINCT ON (COALESCE(team_id, id)) COALESCE(team_id, id) AS TeamId,
                   roster_lineup::text AS Lineup, team_members::text AS Members
            FROM tournament_participants
            WHERE tournament_id = @tournamentId AND status::text IN ('approved', 'confirmed')
              AND (team_id = ANY(@ids) OR id = ANY(@ids))
            ORDER BY COALESCE(team_id, id), created_at DESC, id LIMIT 400
            """, new { tournamentId, ids });
        var rosters = rows.ToDictionary(row => row.TeamId, row => BroadcastRegisteredRoster.Read(row.Lineup, row.Members));
        var playerIds = rosters.Values.SelectMany(roster => roster).Where(player => player.PlayerId.HasValue).Select(player => player.PlayerId!.Value).Distinct().ToArray();
        if (playerIds.Length == 0) return rosters;
        var accounts = (await conn.QueryAsync<VerifiedGameIdentity>("SELECT user_id AS UserId,game_name || '#' || tag_line AS InGameName,riot_puuid AS GameId FROM player_riot_accounts WHERE user_id=ANY(@playerIds) AND verified_at IS NOT NULL", new { playerIds })).ToDictionary(account => account.UserId);
        return rosters.ToDictionary(pair => pair.Key, pair => pair.Value.Select(player => player.PlayerId is { } id && accounts.TryGetValue(id, out var game) ? player with { InGameName = game.InGameName, GameId = game.GameId } : player).ToArray());
    }

    private sealed record RegisteredRosterRow(Guid TeamId, string? Lineup, string? Members);
    private sealed record VerifiedGameIdentity(Guid UserId, string InGameName, string GameId);
}
