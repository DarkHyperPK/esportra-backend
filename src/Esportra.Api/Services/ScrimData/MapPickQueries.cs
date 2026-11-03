using System.Data;
using Dapper;

namespace Esportra.Api.Services.ScrimData;

/// <summary>Reads for the map picks tab: the team's finished vetoes, their actions and the maps played, in three round trips.</summary>
public static class MapPickQueries
{
    public const int SessionCap = 300;

    public static async Task<List<VetoSessionRow>> SessionsAsync(IDbConnection conn, Guid teamId) =>
        (await conn.QueryAsync<VetoSessionRow>(
            """
            SELECT s.id AS SessionId, v.id AS VetoId, CASE WHEN s.team1_id = @teamId THEN 1 ELSE 2 END AS MySlot, gm.map_name AS Decider
            FROM public.scrim_sessions s
            JOIN public.public_veto_sessions v ON v.id = s.veto_session_id AND v.status = 'completed'
            LEFT JOIN public.game_maps gm ON gm.id::text = v.selected_map_id
            WHERE s.team1_id = @teamId OR s.team2_id = @teamId
            ORDER BY v.completed_at DESC NULLS LAST, s.id
            LIMIT @limit
            """, new { teamId, limit = SessionCap })).ToList();

    public static async Task<List<VetoActionRow>> ActionsAsync(IDbConnection conn, Guid[] vetoIds) =>
        vetoIds.Length == 0 ? [] : (await conn.QueryAsync<VetoActionRow>(
            """
            SELECT a.session_id AS VetoId, a.team_side AS TeamSide, a.action_type AS ActionType, gm.map_name AS MapName,
                   a.action_number AS ActionNumber, a.side AS Side
            FROM public.public_veto_actions a
            LEFT JOIN public.game_maps gm ON gm.id::text = a.map_id
            WHERE a.session_id = ANY(@vetoIds)
            """, new { vetoIds })).ToList();

    public static async Task<List<VetoGameRow>> GamesAsync(IDbConnection conn, Guid[] sessionIds) =>
        sessionIds.Length == 0 ? [] : (await conn.QueryAsync<VetoGameRow>(
            """
            SELECT g.session_id AS SessionId, g.game_number AS GameNumber, g.map_name AS MapName, g.team1_score AS Team1Score,
                   g.team2_score AS Team2Score, g.winner_slot AS WinnerSlot, g.status AS Status
            FROM public.scrim_session_games g
            WHERE g.session_id = ANY(@sessionIds)
            """, new { sessionIds })).ToList();
}
