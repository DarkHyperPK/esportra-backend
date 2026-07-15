namespace Esportra.Core.Br;

/// <summary>Static SQL for BR evidence reads — no string interpolation to avoid $"" brace hazards.</summary>
internal static class BrEvidenceSql
{
    internal const string StaffTeamGame = """
        SELECT re.team_id AS team_id,
               t.name AS team_name,
               t.logo_url AS logo_url,
               re.image_url,
               re.submitted_at,
               re.placement,
               re.kills,
               re.reviewed
        FROM br_lobby_evidence re
        LEFT JOIN teams t ON t.id = re.team_id
        WHERE re.game_id = @targetGameId
        ORDER BY re.submitted_at DESC
        """;

    internal const string StaffParticipantGame = """
        SELECT COALESCE(re.team_id, re.participant_id) AS team_id,
               CASE
                   WHEN re.team_id IS NOT NULL THEN t.name
                   ELSE COALESCE(p.username, tp.team_name, t.name, 'Unknown')
               END AS team_name,
               CASE WHEN re.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url,
               re.image_url,
               re.submitted_at,
               re.placement,
               re.kills,
               re.reviewed
        FROM br_lobby_evidence re
        LEFT JOIN teams t ON t.id = re.team_id
        LEFT JOIN tournament_participants tp ON tp.id = re.participant_id
        LEFT JOIN profiles p ON p.id = tp.user_id
        WHERE re.game_id = @targetGameId
        ORDER BY re.submitted_at DESC
        """;

    internal const string StaffTeamLobby = """
        SELECT re.team_id AS team_id,
               t.name AS team_name,
               t.logo_url AS logo_url,
               re.image_url,
               re.submitted_at,
               re.placement,
               re.kills,
               re.reviewed,
               g.game_number
        FROM br_lobby_evidence re
        JOIN br_games g ON g.id = re.game_id
        LEFT JOIN teams t ON t.id = re.team_id
        WHERE g.lobby_id = @lobbyId
        ORDER BY g.game_number ASC, re.submitted_at DESC
        """;

    internal const string StaffParticipantLobby = """
        SELECT COALESCE(re.team_id, re.participant_id) AS team_id,
               CASE
                   WHEN re.team_id IS NOT NULL THEN t.name
                   ELSE COALESCE(p.username, tp.team_name, t.name, 'Unknown')
               END AS team_name,
               CASE WHEN re.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url,
               re.image_url,
               re.submitted_at,
               re.placement,
               re.kills,
               re.reviewed,
               g.game_number
        FROM br_lobby_evidence re
        JOIN br_games g ON g.id = re.game_id
        LEFT JOIN teams t ON t.id = re.team_id
        LEFT JOIN tournament_participants tp ON tp.id = re.participant_id
        LEFT JOIN profiles p ON p.id = tp.user_id
        WHERE g.lobby_id = @lobbyId
        ORDER BY g.game_number ASC, re.submitted_at DESC
        """;

    internal const string PlayerTeamGame = """
        SELECT re.team_id AS team_id,
               t.name AS team_name,
               t.logo_url AS logo_url,
               re.image_url,
               re.submitted_at,
               re.placement,
               re.kills,
               re.reviewed
        FROM br_lobby_evidence re
        LEFT JOIN teams t ON t.id = re.team_id
        WHERE re.game_id = @targetGameId
          AND re.team_id = @viewerTeamId
        ORDER BY re.submitted_at DESC
        """;

    internal const string PlayerParticipantGame = """
        SELECT COALESCE(re.team_id, re.participant_id) AS team_id,
               CASE
                   WHEN re.team_id IS NOT NULL THEN t.name
                   ELSE COALESCE(p.username, tp.team_name, t.name, 'Unknown')
               END AS team_name,
               CASE WHEN re.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url,
               re.image_url,
               re.submitted_at,
               re.placement,
               re.kills,
               re.reviewed
        FROM br_lobby_evidence re
        LEFT JOIN teams t ON t.id = re.team_id
        LEFT JOIN tournament_participants tp ON tp.id = re.participant_id
        LEFT JOIN profiles p ON p.id = tp.user_id
        WHERE re.game_id = @targetGameId
          AND (
            (@viewerTeamId IS NOT NULL AND re.team_id = @viewerTeamId)
            OR (@viewerParticipantId IS NOT NULL AND re.participant_id = @viewerParticipantId)
          )
        ORDER BY re.submitted_at DESC
        """;
}
