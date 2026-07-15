using System.Data;
using Dapper;
using Esportra.Api.Services;
using Esportra.Core.Tournaments;

namespace Esportra.Api.Helpers;

internal static class RosterMemberValidationHelper
{
    private sealed record RosterRow(string? Game, string? Format, int TeamSize);

    private sealed record MemberRow(Guid UserId, string? RosterRole, bool IsStarter);

    public static async Task ValidateCanAddMemberAsync(
        IDbConnection conn,
        GameCatalogService catalog,
        Guid rosterId,
        string rosterRole,
        IDbTransaction? tx = null)
    {
        var (rules, members) = await LoadContextAsync(conn, catalog, rosterId, tx);

        try
        {
            RosterCapacityValidator.ValidateCanAddMember(rules, members, rosterRole);
        }
        catch (InvalidOperationException ex)
        {
            throw new GameCatalogValidationException(ex.Message);
        }
    }

    public static async Task ValidateRoleChangeAsync(
        IDbConnection conn,
        GameCatalogService catalog,
        Guid rosterId,
        Guid userId,
        string newRole,
        IDbTransaction? tx = null)
    {
        var (rules, members) = await LoadContextAsync(conn, catalog, rosterId, tx);
        try
        {
            RosterCapacityValidator.ValidateRoleChange(rules, members, userId, newRole);
        }
        catch (InvalidOperationException ex)
        {
            throw new GameCatalogValidationException(ex.Message);
        }
    }

    public static async Task<string> ResolveRoleForNewMemberAsync(
        IDbConnection conn,
        GameCatalogService catalog,
        Guid rosterId,
        string? requestedRole,
        bool? isStarter,
        IDbTransaction? tx = null)
    {
        var (rules, members) = await LoadContextAsync(conn, catalog, rosterId, tx);
        try
        {
            return RosterCapacityValidator.ResolveRoleForNewMember(rules, members, requestedRole, isStarter);
        }
        catch (InvalidOperationException ex)
        {
            throw new GameCatalogValidationException(ex.Message);
        }
    }

    private static async Task<(RosterModeRules Rules, List<RosterLineupMember> Members)> LoadContextAsync(
        IDbConnection conn,
        GameCatalogService catalog,
        Guid rosterId,
        IDbTransaction? tx)
    {
        var roster = await conn.QuerySingleOrDefaultAsync<RosterRow>(
            """
            SELECT game, format, team_size AS teamSize
            FROM public.team_rosters
            WHERE id = @rosterId
            """,
            new { rosterId },
            tx);

        if (roster is null)
            throw new GameCatalogValidationException("Roster was not found.");

        var resolution = await catalog.ResolveGameModeAsync(
            roster.Game ?? string.Empty,
            roster.Format,
            roster.TeamSize,
            conn,
            tx);

        var rules = RosterCapacityValidator.FromModeResolution(
            resolution.TeamSize,
            resolution.AllowsSubstitutes,
            resolution.MaxRosterSize,
            resolution.MaxSubstitutes,
            resolution.AllowsCoaches,
            resolution.MaxCoaches);

        var rows = (await conn.QueryAsync<MemberRow>(
            """
            SELECT user_id AS userId,
                   roster_role::text AS rosterRole,
                   COALESCE(is_starter, TRUE) AS isStarter
            FROM public.team_roster_members
            WHERE roster_id = @rosterId
            """,
            new { rosterId },
            tx)).AsList();

        var members = rows
            .Select(row => new RosterLineupMember(row.UserId, row.RosterRole ?? "starter", row.IsStarter))
            .ToList();

        return (rules, members);
    }
}
