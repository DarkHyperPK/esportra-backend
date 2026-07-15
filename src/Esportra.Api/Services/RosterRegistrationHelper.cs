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

    public static async Task<(string TeamMembersJson, string RosterLineupJson)> BuildFromSubmittedLineupAsync(
        IDbConnection conn,
        Guid rosterId,
        string rosterLineupJson,
        IDbTransaction? tx = null)
    {
        var parsed = RosterLineupSubmissionParser.Parse(rosterLineupJson);
        var rosterUserIds = (await conn.QueryAsync<Guid>(
            """
            SELECT user_id
            FROM public.team_roster_members
            WHERE roster_id = @rosterId
            """,
            new { rosterId }, tx)).ToHashSet();

        if (parsed.Any(entry => !rosterUserIds.Contains(entry.UserId)))
            throw new InvalidOperationException("Tournament lineup includes a player who is not on the selected roster.");

        var userIds = parsed.Select(entry => entry.UserId).Distinct().ToArray();
        var profiles = (await conn.QueryAsync<(Guid Id, string? Username, string? FullName)>(
            """
            SELECT id AS Id, username AS Username, full_name AS FullName
            FROM public.profiles
            WHERE id = ANY(@userIds)
            """,
            new { userIds }, tx)).ToDictionary(row => row.Id);

        string ResolveName(RosterLineupSubmissionParser.ParsedEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.DisplayName)) return entry.DisplayName.Trim();
            if (profiles.TryGetValue(entry.UserId, out var profile))
            {
                if (!string.IsNullOrWhiteSpace(profile.Username)) return profile.Username.Trim();
                if (!string.IsNullOrWhiteSpace(profile.FullName)) return profile.FullName.Trim();
            }
            return entry.UserId.ToString();
        }

        var payload = new
        {
            starters = parsed
                .Where(entry => entry.Role == "starter")
                .Select(entry => new { userId = entry.UserId.ToString(), displayName = ResolveName(entry) })
                .ToList(),
            substitutes = parsed
                .Where(entry => entry.Role == "substitute")
                .Select(entry => new { userId = entry.UserId.ToString(), displayName = ResolveName(entry) })
                .ToList(),
            coaches = parsed
                .Where(entry => entry.Role == "coach")
                .Select(entry => new { userId = entry.UserId.ToString(), displayName = ResolveName(entry) })
                .ToList(),
        };

        var displayNames = parsed
            .Where(entry => entry.Role is "starter" or "substitute" or "coach")
            .OrderBy(entry => entry.Role switch
            {
                "starter" => 0,
                "substitute" => 1,
                _ => 2,
            })
            .ThenBy(entry => ResolveName(entry), StringComparer.OrdinalIgnoreCase)
            .Select(ResolveName)
            .ToList();

        return (
            JsonSerializer.Serialize(displayNames),
            JsonSerializer.Serialize(payload));
    }
}
