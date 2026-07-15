namespace Esportra.Core.Tournaments;

/// <summary>
/// Shared SQL for resolving bracket match competitor names/logos/kinds including mock and solo participants.
/// Bracket slot columns (team1_id / team2_id) hold either a team UUID or a tournament_participants UUID.
/// </summary>
public static class BracketTeamResolutionSql
{
    public const string Team1Joins = """
        LEFT JOIN teams t1 ON t1.id = m.team1_id
        LEFT JOIN tournament_participants tp1 ON tp1.id = m.team1_id
          AND (tp1.is_mock = TRUE OR tp1.participant_type = 'solo')
        LEFT JOIN profiles sp1 ON sp1.id = tp1.user_id
        """;

    public const string Team2Joins = """
        LEFT JOIN teams t2 ON t2.id = m.team2_id
        LEFT JOIN tournament_participants tp2 ON tp2.id = m.team2_id
          AND (tp2.is_mock = TRUE OR tp2.participant_type = 'solo')
        LEFT JOIN profiles sp2 ON sp2.id = tp2.user_id
        """;

    public const string Team1Columns = """
        COALESCE(t1.name, tp1.team_name, sp1.username) AS team1_name,
        COALESCE(t1.logo_url, sp1.avatar_url) AS team1_logo,
        CASE
          WHEN tp1.id IS NOT NULL AND tp1.is_mock = TRUE THEN 'mock'
          WHEN tp1.id IS NOT NULL AND tp1.participant_type = 'solo' THEN 'solo'
          ELSE COALESCE(t1.team_kind, CASE WHEN COALESCE(t1.is_solo, false) THEN 'solo' ELSE 'team' END)
        END AS team1_kind,
        m.team1_seed
        """;

    public const string Team2Columns = """
        COALESCE(t2.name, tp2.team_name, sp2.username) AS team2_name,
        COALESCE(t2.logo_url, sp2.avatar_url) AS team2_logo,
        CASE
          WHEN tp2.id IS NOT NULL AND tp2.is_mock = TRUE THEN 'mock'
          WHEN tp2.id IS NOT NULL AND tp2.participant_type = 'solo' THEN 'solo'
          ELSE COALESCE(t2.team_kind, CASE WHEN COALESCE(t2.is_solo, false) THEN 'solo' ELSE 'team' END)
        END AS team2_kind,
        m.team2_seed
        """;

    /// <summary>Alias <c>bm</c> for <c>brkt_matches</c> (organizer match detail queries).</summary>
    public const string BracketMatchTeam1Joins = """
        LEFT JOIN teams t1 ON t1.id = bm.team1_id
        LEFT JOIN tournament_participants tp1 ON tp1.id = bm.team1_id
          AND (tp1.is_mock = TRUE OR tp1.participant_type = 'solo')
        LEFT JOIN profiles sp1 ON sp1.id = tp1.user_id
        """;

    public const string BracketMatchTeam2Joins = """
        LEFT JOIN teams t2 ON t2.id = bm.team2_id
        LEFT JOIN tournament_participants tp2 ON tp2.id = bm.team2_id
          AND (tp2.is_mock = TRUE OR tp2.participant_type = 'solo')
        LEFT JOIN profiles sp2 ON sp2.id = tp2.user_id
        """;

    public const string BracketMatchTeam1Columns = """
        COALESCE(t1.name, tp1.team_name, sp1.username) AS team1_name,
        COALESCE(t1.logo_url, sp1.avatar_url) AS team1_logo,
        CASE
          WHEN tp1.id IS NOT NULL AND tp1.is_mock = TRUE THEN 'mock'
          WHEN tp1.id IS NOT NULL AND tp1.participant_type = 'solo' THEN 'solo'
          ELSE COALESCE(t1.team_kind, CASE WHEN COALESCE(t1.is_solo, false) THEN 'solo' ELSE 'team' END)
        END AS team1_kind,
        bm.team1_seed
        """;

    public const string BracketMatchTeam2Columns = """
        COALESCE(t2.name, tp2.team_name, sp2.username) AS team2_name,
        COALESCE(t2.logo_url, sp2.avatar_url) AS team2_logo,
        CASE
          WHEN tp2.id IS NOT NULL AND tp2.is_mock = TRUE THEN 'mock'
          WHEN tp2.id IS NOT NULL AND tp2.participant_type = 'solo' THEN 'solo'
          ELSE COALESCE(t2.team_kind, CASE WHEN COALESCE(t2.is_solo, false) THEN 'solo' ELSE 'team' END)
        END AS team2_kind,
        bm.team2_seed
        """;
}
