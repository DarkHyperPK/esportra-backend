using System.Diagnostics;
using System.Text.Json;
using Esportra.Api.Helpers;
using Esportra.Api.Middleware;
using Esportra.Api.ScoreboardOcr;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Staff-only test bench for the scoreboard OCR service (/debug/riot/ocr). Reads a screenshot
/// without a match and stores nothing: no storage upload, no parse row, no report.
/// </summary>
public static class ScoreboardOcrDebugEndpoints
{
    private const int MaxNamesPerTeam = 15;
    private const int MaxNameLength = 40;

    public static void MapScoreboardOcrDebugEndpoints(this WebApplication app)
    {
        app.MapPost("/api/debug/scoreboard-ocr", ParseAsync)
            .RequireAuthorization("Authenticated")
            .DisableAntiforgery()
            .WithMetadata(new RateLimitPolicyMetadata("screenshotOcr"));

        app.MapGet("/api/debug/scoreboard-ocr/health", HealthAsync)
            .RequireAuthorization("Authenticated");
    }

    private static bool IsStaff(UserContext user) =>
        StaffAuthHelper.IsPlatformAdmin(user)
        || user.Permissions.Contains(Permissions.DisputesView, StringComparer.OrdinalIgnoreCase);

    private static IResult StaffOnly() =>
        Results.Json(new { error = "The OCR test bench is for staff only." }, statusCode: 403);

    private static async Task<IResult> HealthAsync(HttpContext ctx, ScoreboardOcrClient ocr, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (!IsStaff(user)) return StaffOnly();
        var health = await ocr.HealthAsync(ct);
        return Results.Ok(new { configured = ocr.IsConfigured, reachable = health.Reachable, agents = health.Agents });
    }

    private static async Task<IResult> ParseAsync(HttpContext ctx, ScoreboardOcrClient ocr, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (!IsStaff(user)) return StaffOnly();
        if (!ocr.IsConfigured)
            return Results.Json(new { error = "ScoreboardOcr__BaseUrl / ScoreboardOcr__ServiceToken are not set on the API.", configured = false }, statusCode: 503);
        if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Send the screenshot as multipart form data." });

        var form = await ctx.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length is 0 or > ScreenshotValidator.MaxBytes)
            return Results.BadRequest(new { error = "Upload one screenshot of 10 MB or less." });

        var bytes = await ScoreboardOcrEndpoints.ReadAllAsync(file, ct);
        var check = ScreenshotValidator.Validate(file.FileName, bytes);
        if (!check.Ok) return Results.BadRequest(new { error = check.Error });

        var rosters = new OcrRosters(ParseNames(form["team1Names"], "team1"), ParseNames(form["team2Names"], "team2"));
        var timer = Stopwatch.StartNew();
        var call = await ocr.ParseValorantAsync(bytes, $"screenshot{check.Extension}", check.ContentType, rosters, ct);
        timer.Stop();

        if (call.Status == OcrCallStatus.Unreadable) return Results.UnprocessableEntity(new { error = call.UserMessage, elapsedMs = timer.ElapsedMilliseconds });
        if (call.Status != OcrCallStatus.Success) return Results.Json(new { error = call.UserMessage }, statusCode: 502);

        using var result = JsonDocument.Parse(call.ResultJson!);
        return Results.Ok(new { elapsedMs = timer.ElapsedMilliseconds, result = result.RootElement.Clone() });
    }

    /// <summary>Newline-separated names → roster players with synthetic ids (team1-0, team1-1, …).</summary>
    internal static IReadOnlyList<OcrRosterPlayer> ParseNames(string? text, string prefix) =>
        (text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length <= MaxNameLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxNamesPerTeam)
            .Select((name, i) => new OcrRosterPlayer($"{prefix}-{i}", [name]))
            .ToList();
}
