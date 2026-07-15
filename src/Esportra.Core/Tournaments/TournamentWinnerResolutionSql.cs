namespace Esportra.Core.Tournaments;

/// <summary>
/// SQL fragments for resolving tournament winner display from team or native solo participant ids.
/// Requires tournament alias <c>t</c> on <c>tournaments</c>.
/// </summary>
public static class TournamentWinnerResolutionSql
{
    public const string WinnerJoins = """
        LEFT JOIN teams wt ON wt.id = t.winner_id
        LEFT JOIN tournament_participants wtp ON wtp.id = t.winner_id
        LEFT JOIN profiles wp ON wp.id = wtp.user_id
        """;

    public const string WinnerNameColumn = """
        COALESCE(wt.name, wtp.team_name, wp.username) AS winner_team_name
        """;

    public const string WinnerLogoColumn = """
        COALESCE(wt.logo_url, wp.avatar_url) AS winner_team_logo
        """;
}
