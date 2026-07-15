using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Br;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static partial class BRGroupEndpoints
{
    static partial void MapBrConfigRoutes(WebApplication app)
    {
        app.MapGet("/api/stages/{stageId}/br/config", async (
            Guid stageId,
            HttpContext ctx,
            IDbConnectionFactory db,
            GameCatalogService catalog,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx)
                && !await CanViewStagePublicDataAsync(conn, ctx, stageId))
            {
                return Results.Forbid();
            }

            var stage = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT ts.config, ts.advancement_count, t.settings, t.game
                FROM tournament_stages ts
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE ts.id = @stageId
                """,
                new { stageId });

            if (stage is null)
                return Results.NotFound(new { error = "Stage not found." });

            var catalogBrConfig = await LoadCatalogBrConfigAsync(catalog, stage.game as string, ct);
            var gamesModelActive = await BrSchemaRepository.BrGamesModelReadyAsync(conn);
            var resolved = BrConfigService.ResolveForApi(
                stage.settings,
                stage.config,
                catalogBrConfig,
                stage.advancement_count is not null ? Convert.ToInt32(stage.advancement_count) : null,
                gamesModelActive);

            return Results.Ok(new
            {
                gamesModelActive,
                gamesPerLobby = resolved.GamesPerLobby,
                mapScope = resolved.MapScope,
                mapConfig = new
                {
                    mode = resolved.MapConfig.Mode switch
                    {
                        BrMapMode.None => "none",
                        BrMapMode.FixedStage => "fixed_stage",
                        BrMapMode.PerRound => "per_round",
                        BrMapMode.Rotation => "rotation",
                        _ => "none",
                    },
                    pool = resolved.MapConfig.Pool,
                    fixedMap = resolved.MapConfig.FixedMap,
                },
                scoring = new
                {
                    placements = resolved.Scoring.Placements,
                    killPoints = resolved.Scoring.KillPoints,
                    killCap = resolved.Scoring.KillCap,
                },
                tiebreaker = resolved.Tiebreaker,
                format = resolved.Format,
                advancement = new
                {
                    mode = resolved.Advancement.Mode switch
                    {
                        BrAdvancementMode.TopNPerLobby => "top_n_per_lobby",
                        BrAdvancementMode.TopNOverall => "top_n_overall",
                        BrAdvancementMode.Threshold => "threshold",
                        BrAdvancementMode.None => "none",
                        _ => "top_n_per_group",
                    },
                    count = resolved.Advancement.Count,
                },
                lobbyFormation = resolved.LobbyFormation.ToString(),
                leaderboardScope = resolved.LeaderboardScope.ToString(),
                playersPerLobby = resolved.PlayersPerLobby,
            });
        }).RequireAuthorization("Authenticated");
    }
}
