using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Core.Tournaments;

namespace Esportra.Api.Services;

public static class RosterRegistrationHelper
{
    public static async Task<(string TeamMembersJson, string RosterLineupJson)> BuildRegistrationSnapshotAsync(
        IDbConnection conn,
        Guid rosterId,
        IDbTransaction? tx = null)
    {
        var rows = (await conn.QueryAsync<RosterLineupSnapshotHelper.Row>(
            """
            SELECT trm.user_id AS UserId,
                   p.username AS Username,
                   p.full_name AS FullName,
                   trm.roster_role::text AS RosterRole,
                   COALESCE(trm.is_starter, TRUE) AS IsStarter
            FROM public.team_roster_members trm
            JOIN public.profiles p ON p.id = trm.user_id
            WHERE trm.roster_id = @rosterId
            ORDER BY trm.display_order ASC,
                     CASE trm.roster_role
                         WHEN 'starter' THEN 0
                         WHEN 'substitute' THEN 1
                         WHEN 'coach' THEN 2
                         ELSE 3
                     END,
                     p.username ASC
            """,
            new { rosterId }, tx)).AsList();

        var lineup = RosterLineupSnapshotHelper.Build(rows);
        var payload = new
        {
            starters = lineup.Starters,
            substitutes = lineup.Substitutes,
            coaches = lineup.Coaches,
        };

        return (
            JsonSerializer.Serialize(RosterLineupSnapshotHelper.ToDisplayNameList(lineup)),
            JsonSerializer.Serialize(payload));
    }
}
