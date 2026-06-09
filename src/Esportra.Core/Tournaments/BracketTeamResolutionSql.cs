namespace Esportra.Core.Tournaments;

/// <summary>
/// Shared SQL for resolving bracket match team names/logos/kinds including mock participants.
/// </summary>
public static class BracketTeamResolutionSql
{
    public const string Team1Joins = """
        LEFT JOIN teams t1 ON t1.id = m.team1_id
        LEFT JOIN tournament_participants tp1 ON tp1.is_mock = TRUE AND tp1.id = m.team1_id
        """;

    public const string Team2Joins = """
        LEFT JOIN teams t2 ON t2.id = m.team2_id
        LEFT JOIN tournament_participants tp2 ON tp2.is_mock = TRUE AND tp2.id = m.team2_id
        """;

    public const string Team1Columns = """
        COALESCE(t1.name, tp1.team_name) AS team1_name,
        t1.logo_url AS team1_logo,
        COALESCE(t1.team_kind, CASE WHEN COALESCE(t1.is_solo, false) THEN 'solo' ELSE 'team' END) AS team1_kind
        """;

    public const string Team2Columns = """
        COALESCE(t2.name, tp2.team_name) AS team2_name,
        t2.logo_url AS team2_logo,
        COALESCE(t2.team_kind, CASE WHEN COALESCE(t2.is_solo, false) THEN 'solo' ELSE 'team' END) AS team2_kind
        """;

    /// <summary>Alias <c>bm</c> for <c>brkt_matches</c> (organizer match detail queries).</summary>
    public const string BracketMatchTeam1Joins = """
        LEFT JOIN teams t1 ON t1.id = bm.team1_id
        LEFT JOIN tournament_participants tp1 ON tp1.is_mock = TRUE AND tp1.id = bm.team1_id
        """;

    public const string BracketMatchTeam2Joins = """
        LEFT JOIN teams t2 ON t2.id = bm.team2_id
        LEFT JOIN tournament_participants tp2 ON tp2.is_mock = TRUE AND tp2.id = bm.team2_id
        """;
}
