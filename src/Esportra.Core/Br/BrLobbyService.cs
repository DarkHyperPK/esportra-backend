using System.Data;

namespace Esportra.Core.Br;

public static class BrLobbyService
{
    public static async Task ClearScoredStateAsync(
        IDbConnection conn,
        Guid lobbyId,
        IDbTransaction tx) =>
        await BrLobbyRepository.ClearScoredStateAsync(conn, lobbyId, tx);

    public static async Task EnsureGamesAfterResetAsync(
        IDbConnection conn,
        Guid lobbyId,
        Guid stageId,
        object? tournamentSettings,
        object? stageConfig,
        object? catalogBrConfig,
        IDbTransaction tx)
    {
        if (!await BrSchemaRepository.TableExistsAsync(conn, "br_games", tx))
            return;

        var gamesPerLobby = BrConfigService.ResolveGamesPerLobby(tournamentSettings, stageConfig)
            ?? await BrGameRepository.ResolveGamesPerLobbyAsync(conn, stageId, tx);

        await BrGameRepository.EnsureGamesForLobbyAsync(
            conn,
            lobbyId,
            gamesPerLobby,
            tournamentSettings,
            stageConfig,
            catalogBrConfig,
            tx);
    }
}
