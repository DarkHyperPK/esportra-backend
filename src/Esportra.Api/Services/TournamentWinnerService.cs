using System.Data;
using Dapper;

namespace Esportra.Api.Services;

public sealed class TournamentWinnerService
{
    private readonly ILogger<TournamentWinnerService> _logger;

    public TournamentWinnerService(ILogger<TournamentWinnerService> logger)
    {
        _logger = logger;
    }

    public async Task SetWinnerAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        Guid tournamentId,
        Guid winnerId,
        string reason,
        CancellationToken ct = default)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT public.admin_set_tournament_winner(@p_tournament_id, @p_winner_id)",
            new { p_tournament_id = tournamentId, p_winner_id = winnerId },
            tx,
            cancellationToken: ct));

        _logger.LogInformation(
            "Tournament {TournamentId} winner set to {WinnerId}. Reason: {Reason}",
            tournamentId,
            winnerId,
            reason);
    }

    public async Task ClearWinnerAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        Guid tournamentId,
        bool reopenCompleted,
        string reason,
        CancellationToken ct = default)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT public.admin_clear_tournament_winner(@tournamentId, @reopenCompleted)",
            new { tournamentId, reopenCompleted },
            tx,
            cancellationToken: ct));

        _logger.LogInformation(
            "Tournament {TournamentId} winner cleared. ReopenCompleted={ReopenCompleted}. Reason: {Reason}",
            tournamentId,
            reopenCompleted,
            reason);
    }
}
