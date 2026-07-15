using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Requests;
using Esportra.Core.Audit;

namespace Esportra.Api.Endpoints;

public static class GameCatalogAdminEndpoints
{
    public static void MapGameCatalogAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/games/catalog/draft", async (
            HttpContext ctx,
            GameCatalogService catalog,
            CancellationToken ct) =>
        {
            if (!TryRequireGamesView(ctx, out var userCtx, out var authResult))
                return authResult!;

            try
            {
                var draft = await catalog.GetDraftAsync(ct);
                return Results.Ok(draft);
            }
            catch (GameCatalogValidationException)
            {
                return Results.NotFound(new { error = "No catalog draft exists. POST /api/admin/games/catalog/draft to create one." });
            }
        });

        app.MapPost("/api/admin/games/catalog/draft", async (
            HttpContext ctx,
            GameCatalogService catalog,
            AuditService audit,
            CancellationToken ct) =>
        {
            if (!TryRequireGamesManage(ctx, out var userCtx, out var authResult))
                return authResult!;

            try
            {
                var draft = await catalog.ResetDraftFromActiveAsync(userCtx!.UserIdGuid, ct);
                await audit.LogAsync(
                    userCtx.UserIdGuid,
                    userCtx.Email,
                    ActionType.Create,
                    TargetType.System,
                    userCtx.UserIdGuid,
                    "game-catalog-draft",
                    new { action = "reset_draft", gameCount = draft.Games.Count },
                    AuditSeverity.Medium,
                    ct);
                return Results.Ok(draft);
            }
            catch (GameCatalogValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/admin/games/catalog/draft/create", async (
            HttpContext ctx,
            GameCatalogService catalog,
            AuditService audit,
            CancellationToken ct) =>
        {
            if (!TryRequireGamesManage(ctx, out var userCtx, out var authResult))
                return authResult!;

            try
            {
                var draft = await catalog.GetOrCreateDraftAsync(userCtx!.UserIdGuid, ct);
                await audit.LogAsync(
                    userCtx.UserIdGuid,
                    userCtx.Email,
                    ActionType.Create,
                    TargetType.System,
                    userCtx.UserIdGuid,
                    "game-catalog-draft",
                    new { action = "create_draft", gameCount = draft.Games.Count },
                    AuditSeverity.Low,
                    ct);
                return Results.Ok(draft);
            }
            catch (GameCatalogValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPut("/api/admin/games/catalog/draft/games/{slug}", async (
            string slug,
            HttpContext ctx,
            UpsertDraftGameRequest request,
            GameCatalogService catalog,
            CancellationToken ct) =>
        {
            if (!TryRequireGamesManage(ctx, out _, out var authResult))
                return authResult!;

            try
            {
                var game = await catalog.UpsertDraftGameAsync(slug, request, ct);
                return Results.Ok(game);
            }
            catch (GameCatalogValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/admin/games/catalog/draft/games/{slug}", async (
            string slug,
            HttpContext ctx,
            GameCatalogService catalog,
            CancellationToken ct) =>
        {
            if (!TryRequireGamesManage(ctx, out _, out var authResult))
                return authResult!;

            try
            {
                await catalog.DeleteDraftGameAsync(slug, ct);
                return Results.NoContent();
            }
            catch (GameCatalogValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/admin/games/catalog/draft/games/{slug}/logo", async (
            string slug,
            HttpContext ctx,
            GameCatalogService catalog,
            GameCatalogAssetService assets,
            CancellationToken ct) =>
        {
            if (!TryRequireGamesManage(ctx, out _, out var authResult))
                return authResult!;

            var file = ctx.Request.Form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No file provided." });

            try
            {
                var logoUrl = await assets.UploadGameLogoAsync(slug, file, ct);
                await catalog.SetDraftGameLogoAsync(slug, logoUrl, ct);
                return Results.Ok(new { logoUrl });
            }
            catch (GameCatalogValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/admin/games/catalog/publish", async (
            HttpContext ctx,
            PublishCatalogRequest? request,
            GameCatalogService catalog,
            AuditService audit,
            CancellationToken ct) =>
        {
            if (!TryRequireGamesManage(ctx, out var userCtx, out var authResult))
                return authResult!;

            try
            {
                var published = await catalog.PublishDraftAsync(userCtx!.UserIdGuid, request?.Notes, ct);
                await audit.LogAsync(
                    userCtx.UserIdGuid,
                    userCtx.Email,
                    ActionType.Update,
                    TargetType.System,
                    userCtx.UserIdGuid,
                    "game-catalog",
                    new
                    {
                        action = "publish",
                        catalogVersion = published.CatalogVersion,
                        contentHash = published.ContentHash,
                        gameCount = published.Games.Count,
                        notes = request?.Notes,
                    },
                    AuditSeverity.High,
                    ct);
                return Results.Ok(published);
            }
            catch (GameCatalogValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/admin/games/catalog/discard", async (
            HttpContext ctx,
            GameCatalogService catalog,
            AuditService audit,
            CancellationToken ct) =>
        {
            if (!TryRequireGamesManage(ctx, out var userCtx, out var authResult))
                return authResult!;

            await catalog.DiscardDraftAsync(ct);
            await audit.LogAsync(
                userCtx!.UserIdGuid,
                userCtx.Email,
                ActionType.Delete,
                TargetType.System,
                userCtx.UserIdGuid,
                "game-catalog-draft",
                new { action = "discard_draft" },
                AuditSeverity.Medium,
                ct);
            return Results.NoContent();
        });

        app.MapGet("/api/admin/games/catalog/versions", async (
            HttpContext ctx,
            GameCatalogService catalog,
            CancellationToken ct) =>
        {
            if (!TryRequireGamesView(ctx, out _, out var authResult))
                return authResult!;

            var versions = await catalog.GetVersionHistoryAsync(ct);
            return Results.Ok(new { versions });
        });
    }

    private static bool TryRequireGamesView(HttpContext ctx, out UserContext? userCtx, out IResult? failure)
    {
        userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null)
        {
            failure = Results.Unauthorized();
            return false;
        }

        if (!userCtx.Permissions.Contains(Permissions.GamesView)
            && !userCtx.Permissions.Contains(Permissions.GamesManage))
        {
            failure = Results.Forbid();
            return false;
        }

        failure = null;
        return true;
    }

    private static bool TryRequireGamesManage(HttpContext ctx, out UserContext? userCtx, out IResult? failure)
    {
        userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null)
        {
            failure = Results.Unauthorized();
            return false;
        }

        if (!userCtx.Permissions.Contains(Permissions.GamesManage))
        {
            failure = Results.Forbid();
            return false;
        }

        failure = null;
        return true;
    }
}
