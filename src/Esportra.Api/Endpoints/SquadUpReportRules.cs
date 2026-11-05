using Esportra.Api.ScoreboardOcr;

namespace Esportra.Api.Endpoints;

internal sealed record ReportDraft(
    Guid ReportedId, string Reason, string Details, string? ProofLink, Guid? MatchId, string Context, bool Hide, Guid? LobbyId = null);

internal sealed record ReportImage(byte[] Data, string Extension, string ContentType);

/// <summary>Validation for Squad Up player reports. Everything is checked before anything is stored.</summary>
internal static class SquadUpReportRules
{
    public const int MaxFiles = 5;
    public const int MaxDetails = 1000;
    public const int MaxLink = 500;
    public const int HourlyLimit = 5;

    public static readonly IReadOnlySet<string> Reasons = new HashSet<string>
    {
        "harassment", "hate_speech", "cheating", "fake_rank", "spam_scam", "inappropriate_profile", "other",
    };

    /// <returns>The draft, or an error code for a 400.</returns>
    public static (ReportDraft? Draft, string? Error) ParseDraft(IFormCollection form, Guid reporterId)
    {
        if (!Guid.TryParse(form["reported_user_id"], out var reportedId) || reportedId == Guid.Empty) return (null, "INVALID_REPORTED_USER");
        if (reportedId == reporterId) return (null, "SELF_REPORT");
        var reason = form["reason"].ToString().Trim().ToLowerInvariant();
        if (!Reasons.Contains(reason)) return (null, "INVALID_REASON");
        var details = form["details"].ToString().Trim();
        if (details.Length > MaxDetails) return (null, "DETAILS_TOO_LONG");
        if (reason == "other" && details.Length < 10) return (null, "DETAILS_REQUIRED");
        var link = form["proof_link"].ToString().Trim();
        if (link.Length > 0 && !IsHttpsLink(link)) return (null, "INVALID_PROOF_LINK");
        Guid? matchId = Guid.TryParse(form["match_id"], out var parsedMatch) ? parsedMatch : null;
        Guid? lobbyId = Guid.TryParse(form["lobby_id"], out var parsedLobby) ? parsedLobby : null;
        var context = lobbyId is not null ? "lobby" : matchId is null ? "card" : "match";
        var hide = bool.TryParse(form["hide_player"], out var hideValue) && hideValue;
        return (new ReportDraft(reportedId, reason, details, link.Length == 0 ? null : link, matchId, context, hide, lobbyId), null);
    }

    public static bool IsHttpsLink(string link) =>
        link.Length <= MaxLink && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Reads every attached screenshot through the shared validator (size, extension and magic bytes).</summary>
    public static async Task<(List<ReportImage> Images, string? Error)> ReadImagesAsync(
        IReadOnlyList<IFormFile> files, CancellationToken ct)
    {
        var images = new List<ReportImage>();
        if (files.Count > MaxFiles) return (images, "TOO_MANY_FILES");
        foreach (var file in files)
        {
            if (file.Length is 0 or > ScreenshotValidator.MaxBytes) return (images, "INVALID_IMAGE");
            var bytes = await ScoreboardOcrEndpoints.ReadAllAsync(file, ct);
            var check = ScreenshotValidator.Validate(file.FileName, bytes);
            if (!check.Ok) return (images, "INVALID_IMAGE");
            images.Add(new ReportImage(bytes, check.Extension, check.ContentType));
        }
        return (images, null);
    }
}
