namespace Esportra.Api.Helpers;

/// <summary>API compatibility shim — logic lives in <see cref="Esportra.Core.Br.BrPlayerContextService"/>.</summary>
public static class BrPlayerContextHelper
{
    public sealed record PlayerGroupRow(
        Guid StageId,
        string StageName,
        int StageOrder,
        Guid GroupId,
        string GroupName);

    public static Task<Esportra.Core.Br.BrPlayerContextService.PlayerGroupRow?> FindPlayerGroupAsync(
        System.Data.IDbConnection conn,
        Guid tournamentId,
        Guid userId,
        System.Data.IDbTransaction? tx = null) =>
        Esportra.Core.Br.BrPlayerContextService.FindPlayerGroupAsync(conn, tournamentId, userId, tx);

    public static Task<bool> IsUserAssignedToGroupAsync(
        System.Data.IDbConnection conn,
        Guid tournamentId,
        Guid groupId,
        Guid userId,
        System.Data.IDbTransaction? tx = null) =>
        Esportra.Core.Br.BrPlayerContextService.IsUserAssignedToGroupAsync(conn, tournamentId, groupId, userId, tx);

    public static Task<string> ResolveAssignmentHintAsync(
        System.Data.IDbConnection conn,
        Guid tournamentId,
        Guid userId,
        System.Data.IDbTransaction? tx = null) =>
        Esportra.Core.Br.BrPlayerContextService.ResolveAssignmentHintAsync(conn, tournamentId, userId, tx);
}
