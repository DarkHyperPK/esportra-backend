using System.Data;
using Dapper;

namespace Esportra.Api.ScoreboardOcr;

/// <summary>Resolved provenance for a match result report.</summary>
public sealed record ReportSource(string Source, Guid? OcrParseId, string? OcrScreenshotPath);

/// <summary>
/// Decides the report's source (riot / manual / ocr). An "ocr" report must point at a parse the same
/// user ran for the same match, game and competitor, so the stored raw OCR output is server-authored.
/// </summary>
public static class ReportSourceResolver
{
    private static readonly TimeSpan MaxParseAge = TimeSpan.FromHours(24);

    private sealed record ParseRow(Guid Id, string ScreenshotPath, DateTime CreatedAt);

    public static async Task<(ReportSource? Source, string? Error)> ResolveAsync(
        IDbConnection conn,
        Guid matchId,
        int gameNumber,
        Guid userId,
        Guid competitorId,
        string? requestedSource,
        string? ocrParseId,
        string? riotMatchId)
    {
        var source = string.IsNullOrWhiteSpace(requestedSource)
            ? (string.IsNullOrWhiteSpace(riotMatchId) ? "manual" : "riot")
            : requestedSource.Trim().ToLowerInvariant();

        if (source is "riot" or "manual")
            return string.IsNullOrWhiteSpace(ocrParseId)
                ? (new ReportSource(source, null, null), null)
                : (null, "ocrParseId is only allowed for screenshot reports.");

        if (source != "ocr")
            return (null, "Unknown report source.");

        if (!Guid.TryParse(ocrParseId, out var parseId))
            return (null, "Screenshot reports need the parse they came from.");

        var parse = await conn.QuerySingleOrDefaultAsync<ParseRow>(
            """
            SELECT id AS Id, screenshot_path AS ScreenshotPath, created_at AS CreatedAt
            FROM scoreboard_ocr_parses
            WHERE id = @parseId AND match_id = @matchId AND game_number = @gameNumber
              AND requested_by = @userId AND competitor_id = @competitorId
            """,
            new { parseId, matchId, gameNumber, userId, competitorId });

        if (parse is null)
            return (null, "That screenshot reading does not belong to this match report.");
        if (DateTime.UtcNow - DateTime.SpecifyKind(parse.CreatedAt, DateTimeKind.Utc) > MaxParseAge)
            return (null, "That screenshot reading has expired. Upload the screenshot again.");

        return (new ReportSource("ocr", parse.Id, parse.ScreenshotPath), null);
    }

    /// <summary>Ensures the screenshot that was read is part of the report's evidence.</summary>
    public static string[] WithOcrScreenshot(string[]? screenshotUrls, string? ocrScreenshotUrl)
    {
        var urls = screenshotUrls ?? [];
        if (string.IsNullOrEmpty(ocrScreenshotUrl) || urls.Contains(ocrScreenshotUrl, StringComparer.Ordinal))
            return urls;
        return [ocrScreenshotUrl, .. urls];
    }
}
