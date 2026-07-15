namespace Esportra.Core.Tournaments;

/// <summary>
/// SQL fragments for filtering real user-managed teams (excludes solo/mock adapter rows).
/// Requires table alias <c>t</c> on <c>teams</c>.
/// </summary>
public static class TeamKindSql
{
    public const string RealTeamWhere = """
        COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) = 'team'
        AND COALESCE(t.is_solo, false) = false
        AND COALESCE(t.tag, '') NOT LIKE 'mock-%'
        """;

    public const string TeamKindSelect = """
        COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END)
        """;
}
