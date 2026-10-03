using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Esportra.Api.ScoreboardOcr;

public sealed record OcrRosterPlayer(string UserId, IReadOnlyList<string> Names);

public sealed record OcrRosters(IReadOnlyList<OcrRosterPlayer> Team1, IReadOnlyList<OcrRosterPlayer> Team2);

public enum OcrCallStatus
{
    Success,
    Unreadable,
    Unavailable,
}

/// <summary>Outcome of one OCR call. <see cref="ResultJson"/> is set only on success.</summary>
public sealed record OcrCallResult(OcrCallStatus Status, string? ResultJson, string? UserMessage);

/// <summary>Typed HTTP client for the internal scoreboard OCR service.</summary>
public sealed class ScoreboardOcrClient(HttpClient http, IOptions<ScoreboardOcrOptions> options, ILogger<ScoreboardOcrClient> logger)
{
    private const int MaxResultBytes = 256 * 1024;
    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<OcrCallResult> ParseValorantAsync(
        byte[] image, string fileName, string contentType, OcrRosters rosters, CancellationToken ct)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.BaseUrl), "/v1/valorant/scoreboard"));
        request.Headers.Add("X-Service-Token", settings.ServiceToken);
        var imageContent = new ByteArrayContent(image);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Content = new MultipartFormDataContent
        {
            { imageContent, "image", fileName },
            { new StringContent(JsonSerializer.Serialize(rosters, CamelCase)), "rosters" },
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return await ReadResponseAsync(response, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Scoreboard OCR service call failed");
            return Unavailable();
        }
    }

    private async Task<OcrCallResult> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > MaxResultBytes)
        {
            logger.LogWarning("Scoreboard OCR response too large ({Length} chars)", body.Length);
            return Unavailable();
        }

        if (response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.RequestEntityTooLarge)
            return new OcrCallResult(OcrCallStatus.Unreadable, null, ReadDetail(body) ?? "We couldn't read a scoreboard from this screenshot.");

        if (!response.IsSuccessStatusCode || !IsScoreboardResult(body))
        {
            logger.LogWarning("Scoreboard OCR service returned {Status}", (int)response.StatusCode);
            return Unavailable();
        }

        return new OcrCallResult(OcrCallStatus.Success, body, null);
    }

    private static OcrCallResult Unavailable() =>
        new(OcrCallStatus.Unavailable, null, "Screenshot reading is unavailable right now. Enter the result manually.");

    private static bool IsScoreboardResult(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("players", out var players)
                && players.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadDetail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String
                ? detail.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
