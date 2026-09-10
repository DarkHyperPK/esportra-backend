using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Match;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

public static class VetoSettingsEndpoints
{
    public static void MapVetoSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/veto/{matchId}/settings", async (
            Guid matchId,
            HttpContext ctx,
            IDbConnectionFactory db,
            VetoDbService veto,
            VetoSettingsService vetoSettings,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            if (!await StaffAuthHelper.CanAccessMatchRoomAsync(conn, userCtx.UserIdGuid, matchId, userCtx))
                return Results.Forbid();

            var dto = await vetoSettings.GetAsync(matchId, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        }).RequireAuthorization("Authenticated").WithTags("Veto");

        app.MapPut("/api/veto/{matchId}/settings", async (
            Guid matchId,
            [FromBody] VetoSettingsSaveRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            VetoDbService veto,
            VetoSettingsService vetoSettings,
            IHubContext<VetoHub> hub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var existing = await veto.GetAsync(matchId, ct);
            if (existing is null) return Results.NotFound();

            if (!await veto.IsOrganizerAsync(userCtx.UserIdGuid, existing.TournamentId, ct)
                && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Json(new { error = "Only the tournament organizer can change veto settings." }, statusCode: 403);

            if (!Enum.TryParse<VetoMode>(req.Mode, ignoreCase: true, out var mode))
                return Results.BadRequest(new { error = "Invalid mode. Use 'default' or 'custom'." });

            try
            {
                await vetoSettings.SaveAsync(matchId, mode, req.Sequence, userCtx.UserIdGuid, ct);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("CONFLICT"))
            {
                return Results.Conflict(new { error = "Cannot change veto settings while a veto is in progress." });
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("INVALID_SEQUENCE"))
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var dto = await vetoSettings.GetAsync(matchId, ct);
            if (dto is null) return Results.NotFound();

            await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                .SendAsync(VetoHubEvents.VetoSettingsSync, dto, ct);

            return Results.Ok(dto);
        }).RequireAuthorization("Authenticated").WithTags("Veto");
    }
}

public sealed record VetoSettingsSaveRequest(string Mode, VetoStep[]? Sequence = null);
