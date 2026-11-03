using System.Text.Json;

namespace Esportra.Api.Services.ScrimData;

/// <summary>
/// Reads the scoreboard a team's screenshot report stored on a scrim map. This is what the screenshot showed, read by OCR and
/// confirmed by the other team only as a result, so it is shown as limited data and never used for round-level figures.
/// </summary>
public static class OcrScoreboard
{
    public static IReadOnlyList<ScrimPlayerLine> TeamLines(Guid gameId, string? matchDataJson, int teamSlot, int roundsPlayed)
    {
        if (string.IsNullOrWhiteSpace(matchDataJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(matchDataJson);
            if (!doc.RootElement.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array) return [];
            var flag = teamSlot == 1 ? "isTeam1" : "isTeam2";
            return players.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object && Bool(p, flag) == true)
                .Select(p => Line(gameId, p, roundsPlayed)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ScrimPlayerLine Line(Guid gameId, JsonElement p, int rounds) =>
        new(gameId, DataTier.Screenshot, GuidOf(p, "userId"), Str(p, "gameName"), Agent(p), null,
            Int(p, "kills"), Int(p, "deaths"), Int(p, "assists"), Num(p, "acs"), rounds > 0 ? rounds : null, Num(p, "adr"), Num(p, "hsPct"),
            Int(p, "firstBloods"), null, Int(p, "plants"), Int(p, "defuses"));

    private static string? Agent(JsonElement p)
    {
        var id = Str(p, "characterId")?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(id) ? null : id;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    private static int? Int(JsonElement e, string name) => Num(e, name) is { } d ? (int)Math.Round(d) : null;

    private static Guid? GuidOf(JsonElement e, string name) => Str(e, name) is { } s && Guid.TryParse(s, out var g) ? g : null;
}
