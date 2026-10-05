using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Middleware;
using Esportra.Api.ScoreboardOcr;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.Extensions.Caching.Hybrid;

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
        return Results.Ok(new
        {
            configured = ocr.IsConfigured,
            reachable = health.Reachable,
            agents = health.Agents,
            bankAgents = health.BankAgents,
            scripts = health.Scripts ?? [],
            missingScripts = health.MissingScripts ?? new Dictionary<string, string>(),
            catalogVersion = health.CatalogVersion,
            refreshedAt = health.RefreshedAt,
        });
    }

    private static readonly TimeSpan DebugWindow = TimeSpan.FromDays(30);

    private static async Task<IResult> ParseAsync(HttpContext ctx, ScoreboardOcrClient ocr, IRiotMatchLookup riotMatches, IDbConnectionFactory db, HybridCache cache, CancellationToken ct)
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
        using var conn = db.CreateConnection();
        var azureEnabled = await cache.GetOrCreateAsync(
            "ocr:engine-setting",
            async _ => await conn.ExecuteScalarAsync<string>(
                "SELECT value FROM system_settings WHERE key = 'azure_ocr_enabled'") ?? "false",
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(60) },
            cancellationToken: ct);
        var engine = azureEnabled == "true" ? "azure" : "paddle";
        var timer = Stopwatch.StartNew();
        var call = await ocr.ParseValorantAsync(bytes, $"screenshot{check.Extension}", check.ContentType, rosters, engine, ct);
        timer.Stop();

        if (call.Status == OcrCallStatus.Unreadable) return Results.UnprocessableEntity(new { error = call.UserMessage, elapsedMs = timer.ElapsedMilliseconds });
        if (call.Status != OcrCallStatus.Success) return Results.Json(new { error = call.UserMessage }, statusCode: 502);

        using var result = JsonDocument.Parse(call.ResultJson!);
        // Debug rosters count as verified Riot IDs, so the bench shows what verification would say.
        var verification = ScoreboardVerification.FromParse(result.RootElement, rosters);
        var riot = await FindRiotMatchAsync(riotMatches, form["riotId"], result.RootElement, ct);
        return Results.Ok(new
        {
            elapsedMs = timer.ElapsedMilliseconds,
            result = result.RootElement.Clone(),
            verification = JsonSerializer.SerializeToElement(verification, OcrVerificationJson.Options),
            riot = riot is null ? null : new { status = riot.Status, match = riot.Match is null ? null : ScoreboardRiotMatcher.Payload(riot.Match, []) },
        });
    }

    /// <summary>
    /// The screenshot's game in the match history of the Riot ID typed on the bench, to compare the read
    /// with Riot's numbers. Test screenshots can be older than a live report, hence the wider window.
    /// </summary>
    private static async Task<RiotLookupOutcome?> FindRiotMatchAsync(IRiotMatchLookup riotMatches, string? riotId, JsonElement result, CancellationToken ct)
    {
        var parts = (riotId ?? "").Trim().Split('#', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length is 0 or > MaxNameLength || parts[1].Length is 0 or > 10) return null;
        var puuid = await riotMatches.PuuidOfAsync(parts[0], parts[1], ct);
        if (puuid is null) return new RiotLookupOutcome(RiotLookupState.NotFound, null);
        var query = ScoreboardRiotMatcher.QueryFrom(result, DateTimeOffset.UtcNow, DebugWindow);
        return await ScoreboardRiotMatcher.FindAsync(riotMatches, [puuid], query, TimeSpan.FromSeconds(15), ct);
    }

    /// <summary>Newline-separated names → roster players with synthetic ids (team1-0, team1-1, …).</summary>
    internal static IReadOnlyList<OcrRosterPlayer> ParseNames(string? text, string prefix) =>
        (text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length <= MaxNameLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxNamesPerTeam)
            .Select((name, i) => new OcrRosterPlayer($"{prefix}-{i}", [new OcrRosterName(name, OcrNameSource.RiotVerified)]))
            .ToList();
}
