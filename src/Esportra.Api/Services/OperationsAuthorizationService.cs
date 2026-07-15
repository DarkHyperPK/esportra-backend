using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;

namespace Esportra.Api.Services;

public sealed class OperationsAuthorizationService(IDbConnectionFactory db)
{
    public bool HasPermission(UserContext userCtx, string permission) =>
        userCtx.IsSuperAdmin ||
        userCtx.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

    public async Task<bool> CanMutateTournamentAsync(
        UserContext userCtx,
        Guid tournamentId,
        string requiredPermission,
        CancellationToken ct = default)
    {
        if (!HasPermission(userCtx, requiredPermission))
            return false;
        if (userCtx.IsSuperAdmin)
            return true;

        using var conn = db.CreateConnection();
        var gameId = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT game FROM public.tournaments WHERE id = @tournamentId",
            new { tournamentId },
            cancellationToken: ct));
        if (string.IsNullOrWhiteSpace(gameId))
            return false;

        var assignmentExists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.admin_game_assignments
                WHERE admin_id = @adminId
                  AND scope = 'tournament_ops'
                  AND lower(game_id) = lower(@gameId)
                  AND (expires_at IS NULL OR expires_at > now())
            )
            """,
            new { adminId = userCtx.UserIdGuid, gameId },
            cancellationToken: ct));

        return assignmentExists;
    }
}
