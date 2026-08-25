using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Esportra.Api.Services;

public sealed class TournamentWinnerService
{
    private readonly ILogger<TournamentWinnerService> _logger;
    private readonly IEnumerable<Esportra.Core.Tournaments.ILeaderboardSourceChangeHook>? _leaderboardHooks;

    public TournamentWinnerService(
        ILogger<TournamentWinnerService> logger,
        IEnumerable<Esportra.Core.Tournaments.ILeaderboardSourceChangeHook>? leaderboardHooks = null)
    {
        _logger = logger;
        _leaderboardHooks = leaderboardHooks;
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

        await Esportra.Core.Tournaments.LeaderboardSourceChangeHooks.FireAsync(
            _leaderboardHooks, _logger, $"winner-set:{tournamentId}", ct);
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

        await Esportra.Core.Tournaments.LeaderboardSourceChangeHooks.FireAsync(
            _leaderboardHooks, _logger, $"winner-clear:{tournamentId}", ct);
    }
}
