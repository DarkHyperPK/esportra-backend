using System.Data;
using Dapper;

namespace Esportra.Core.Tournaments;

/// <summary>
/// Removes mock tournament participants and their backing <c>teams</c> rows
/// for a tournament.
/// </summary>
public static class MockTeamCleanup
{
    public static async Task DeleteForTournamentAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        Guid tournamentId,
        CancellationToken ct = default)
    {
        var hasMockWinner = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.tournaments t
                WHERE t.id = @tournamentId
                  AND t.winner_id IS NOT NULL
                  AND EXISTS (
                      SELECT 1
                      FROM public.tournament_participants tp
                      WHERE tp.tournament_id = t.id
                        AND tp.team_id = t.winner_id
                        AND COALESCE(tp.is_mock, FALSE) = TRUE
                  )
            )
            """,
            new { tournamentId },
            tx,
            cancellationToken: ct));

        if (hasMockWinner)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "SELECT public.admin_clear_tournament_winner(@tournamentId, @reopenCompleted)",
                new { tournamentId, reopenCompleted = true },
                tx,
                cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM public.br_group_teams gt
            USING public.tournament_participants tp
            JOIN public.tournament_stages ts ON ts.tournament_id = tp.tournament_id
            JOIN public.br_groups g ON g.stage_id = ts.id
            WHERE gt.group_id = g.id
              AND gt.participant_id = tp.id
              AND tp.tournament_id = @tournamentId
              AND COALESCE(tp.is_mock, FALSE) = TRUE
            """,
            new { tournamentId },
            tx,
            cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            """
            WITH deleted_mock_participants AS (
                DELETE FROM public.tournament_participants
                WHERE tournament_id = @tournamentId
                  AND is_mock = TRUE
                RETURNING team_id
            )
            DELETE FROM public.teams t
            USING deleted_mock_participants d
            WHERE t.id = d.team_id
              AND COALESCE(t.team_kind, CASE WHEN t.tag LIKE 'mock-%' THEN 'mock' ELSE 'team' END) = 'mock'
            """,
            new { tournamentId },
            tx,
            cancellationToken: ct));
    }

    public static async Task DeleteForTournamentsAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        IEnumerable<Guid> tournamentIds,
        CancellationToken ct = default)
    {
        foreach (var tournamentId in tournamentIds)
            await DeleteForTournamentAsync(conn, tx, tournamentId, ct);
    }
}
