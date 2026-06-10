using System.Data;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>Shared authorization helpers for SignalR hub join methods.</summary>
public static class HubAuthHelper
{
    public static UserContext? GetUserContext(HubCallerContext context) =>
        context.GetHttpContext()?.Items["UserContext"] as UserContext;

    public static async Task<bool> EnsureMatchRoomAccessAsync(
        HubCallerContext context,
        IDbConnectionFactory db,
        string matchIdRaw)
    {
        var userCtx = GetUserContext(context);
        if (userCtx is null || !Guid.TryParse(matchIdRaw, out var matchId))
            return false;

        using var conn = db.CreateConnection();
        return await StaffAuthHelper.CanAccessMatchRoomAsync(conn, userCtx.UserIdGuid, matchId, userCtx);
    }

    public static async Task<bool> EnsureBracketVersionAccessAsync(
        HubCallerContext context,
        IDbConnectionFactory db,
        string versionIdRaw)
    {
        var userCtx = GetUserContext(context);
        if (userCtx is null || !Guid.TryParse(versionIdRaw, out var versionId))
            return false;

        using var conn = db.CreateConnection();
        return await StaffAuthHelper.CanViewBracketVersionAsync(conn, userCtx.UserIdGuid, versionId, userCtx);
    }

    public static async Task<bool> EnsureTournamentSubscriptionAccessAsync(
        HubCallerContext context,
        IDbConnectionFactory db,
        string tournamentIdRaw)
    {
        var userCtx = GetUserContext(context);
        if (userCtx is null || !Guid.TryParse(tournamentIdRaw, out var tournamentId))
            return false;

        using var conn = db.CreateConnection();
        return await StaffAuthHelper.CanViewTournamentAsync(conn, userCtx.UserIdGuid, tournamentId, userCtx);
    }
}
