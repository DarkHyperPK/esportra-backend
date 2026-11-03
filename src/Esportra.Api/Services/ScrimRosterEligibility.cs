using System.Data;
using Dapper;

namespace Esportra.Api.Services;

/// <summary>
/// A team may list or play scrims in a game only with a full roster for that game: a roster for the game whose
/// active, starting members reach the roster's team size (5 when the roster does not say).
/// </summary>
public static class ScrimRosterEligibility
{
    public const int DefaultTeamSize = 5;

    private const string EligibleGamesSql =
        """
        SELECT DISTINCT lower(tr.game)
        FROM public.team_rosters tr
        WHERE tr.team_id = @teamId
          AND tr.game IS NOT NULL
          AND (
              SELECT count(*)
              FROM public.team_roster_members trm
              JOIN public.team_members tm ON tm.team_id = tr.team_id AND tm.user_id = trm.user_id AND tm.is_active = TRUE
              WHERE trm.roster_id = tr.id AND COALESCE(trm.is_starter, TRUE)
          ) >= COALESCE(NULLIF(tr.team_size, 0), @defaultSize)
        """;

    /// <summary>The games (lower-case slugs) the team has a full roster for.</summary>
    public static async Task<IReadOnlyList<string>> EligibleGamesAsync(IDbConnection conn, Guid teamId) =>
        (await conn.QueryAsync<string>(EligibleGamesSql, new { teamId, defaultSize = DefaultTeamSize })).ToList();

    public static async Task<bool> IsEligibleAsync(IDbConnection conn, Guid teamId, string? gameSlug) =>
        !string.IsNullOrWhiteSpace(gameSlug)
        && (await EligibleGamesAsync(conn, teamId)).Contains(gameSlug.Trim().ToLowerInvariant());
}
