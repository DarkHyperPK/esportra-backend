using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Esportra.Api.Middleware;
using Esportra.Api.ScoreboardOcr;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Tournaments;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Screenshot scoreboard reading (Valorant). The result only pre-fills a report: the captain
/// confirms it and the opponent still accepts or disputes it through the normal report flow.
/// </summary>
public static class ScoreboardOcrEndpoints
{
    private sealed record MatchRow(Guid Team1Id, Guid Team2Id, string? Game);

    private sealed record ParseRequest(Guid CompetitorId, int GameNumber, IFormFile File);

    private sealed record ParseRow(
        Guid ParseId, Guid MatchId, ParseRequest Request, Guid UserId, string Path, byte[] Bytes, string ResultJson, JsonElement Result);

    public static void MapScoreboardOcrEndpoints(this WebApplication app)
    {
        app.MapPost("/api/matches/{id}/reports/parse-screenshot", ParseScreenshotAsync)
            .RequireAuthorization("Authenticated")
            .DisableAntiforgery()
            .WithMetadata(new RateLimitPolicyMetadata("screenshotOcr"));
    }

    private static async Task<IResult> ParseScreenshotAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        ScoreboardOcrClient ocr,
        ScoreboardScreenshotStore store,
        CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext userCtx) return Results.Unauthorized();
        if (!ocr.IsConfigured)
            return Results.Json(new { error = "Screenshot reading is not available right now." }, statusCode: 503);

        var (request, formError) = await ReadRequestAsync(ctx, ct);
        if (request is null) return Results.BadRequest(new { error = formError });

        using var conn = db.CreateConnection();
        var (match, denied) = await AuthorizeAsync(conn, userCtx.UserIdGuid, id, request.CompetitorId);
        if (match is null) return denied!;

        var bytes = await ReadAllAsync(request.File, ct);
        var check = ScreenshotValidator.Validate(request.File.FileName, bytes);
        if (!check.Ok) return Results.BadRequest(new { error = check.Error });

        var rosters = await ScoreboardRosterLoader.LoadAsync(conn, match.Team1Id, match.Team2Id);
        var call = await ocr.ParseValorantAsync(bytes, $"screenshot{check.Extension}", check.ContentType, rosters, ct);
        if (call.Status == OcrCallStatus.Unreadable) return Results.UnprocessableEntity(new { error = call.UserMessage });
        if (call.Status != OcrCallStatus.Success) return Results.Json(new { error = call.UserMessage }, statusCode: 502);

        var parseId = Guid.NewGuid();
        var path = ScoreboardScreenshotStore.BuildPath(id, parseId, check.Extension);
        if (!await store.UploadAsync(path, bytes, check.ContentType, ct))
            return Results.Json(new { error = "Could not store the screenshot. Please try again." }, statusCode: 502);

        using var result = JsonDocument.Parse(call.ResultJson!);
        await InsertParseAsync(conn, new ParseRow(parseId, id, request, userCtx.UserIdGuid, path, bytes, call.ResultJson!, result.RootElement));
        return Results.Ok(new
        {
            parseId,
            screenshotUrl = store.PublicUrl(path),
            mapId = await ResolveMapIdAsync(conn, result.RootElement),
            team1Id = match.Team1Id,
            team2Id = match.Team2Id,
            result = result.RootElement.Clone(),
        });
    }

    private static async Task<(ParseRequest? Request, string? Error)> ReadRequestAsync(HttpContext ctx, CancellationToken ct)
    {
        if (!ctx.Request.HasFormContentType) return (null, "Send the screenshot as multipart form data.");
        var form = await ctx.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (!Guid.TryParse(form["reportedByTeamId"].FirstOrDefault(), out var competitorId))
            return (null, "Invalid competitor ID");
        if (!int.TryParse(form["gameNumber"].FirstOrDefault(), out var gameNumber) || gameNumber is < 1 or > 9)
            return (null, "Invalid game number.");
        if (file is null || file.Length is 0 or > ScreenshotValidator.MaxBytes)
            return (null, "Upload one screenshot of 10 MB or less.");
        return (new ParseRequest(competitorId, gameNumber, file), null);
    }

    private static async Task<(MatchRow? Match, IResult? Denied)> AuthorizeAsync(
        IDbConnection conn, Guid userId, Guid matchId, Guid competitorId)
    {
        if (!await BracketCompetitorResolver.CanUserReportForCompetitorAsync(conn, userId, matchId, competitorId))
            return (null, Results.Json(new { error = "Only a captain or solo participant in this match can submit a report." }, statusCode: 403));

        var match = await conn.QuerySingleOrDefaultAsync<MatchRow>(
            """
            SELECT m.team1_id AS Team1Id, m.team2_id AS Team2Id, t.game AS Game
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            JOIN tournaments t ON t.id = v.tournament_id
            WHERE m.id = @matchId
            """,
            new { matchId });
        if (match is null) return (null, Results.NotFound(new { error = "Match not found." }));
        if (match.Game?.Contains("valorant", StringComparison.OrdinalIgnoreCase) != true)
            return (null, Results.BadRequest(new { error = "Screenshot reading currently supports Valorant only." }));
        return (match, null);
    }

    private static Task InsertParseAsync(IDbConnection conn, ParseRow row) =>
        conn.ExecuteAsync(
            """
            INSERT INTO scoreboard_ocr_parses
              (id, match_id, game_number, requested_by, competitor_id, screenshot_path, screenshot_sha256, result, engine_version)
            VALUES (@ParseId, @MatchId, @GameNumber, @UserId, @CompetitorId, @Path, @Sha, @ResultJson::jsonb, @Engine)
            """,
            new
            {
                row.ParseId,
                row.MatchId,
                row.Request.GameNumber,
                row.UserId,
                row.Request.CompetitorId,
                row.Path,
                Sha = Convert.ToHexStringLower(SHA256.HashData(row.Bytes)),
                row.ResultJson,
                Engine = ReadString(row.Result, "engineVersion"),
            });

    private static async Task<byte[]> ReadAllAsync(IFormFile file, CancellationToken ct)
    {
        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private static async Task<Guid?> ResolveMapIdAsync(IDbConnection conn, JsonElement result)
    {
        var name = result.TryGetProperty("map", out var map) && map.TryGetProperty("value", out var value)
            && value.ValueKind == JsonValueKind.Object
            ? ReadString(value, "name")
            : null;
        if (string.IsNullOrWhiteSpace(name)) return null;
        return await conn.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT id FROM game_maps
            WHERE game ILIKE '%valorant%' AND is_active = TRUE AND lower(map_name) = lower(@name)
            LIMIT 1
            """,
            new { name });
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
