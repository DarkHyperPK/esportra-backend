using System.Text.Json;

namespace Esportra.Api.Services;

/// <summary>Reads Riot's published map list (valorant-api.com/v1/maps) into names the map table can learn.</summary>
public static class ValorantMapCatalog
{
    public const string Url = "https://valorant-api.com/v1/maps";

    /// <summary>
    /// Maps in the list: the last segment of each map path, its display name and whether it looks like a competitive-style map
    /// (it has sites, so a tactical description, and is not one of the deathmatch or practice maps).
    /// </summary>
    public static IReadOnlyList<(string Code, string Name, bool Competitive)> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var maps = new List<(string, string, bool)>();
        foreach (var m in data.EnumerateArray())
        {
            var path = Str(m, "mapUrl");
            var name = Str(m, "displayName");
            var code = path?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) continue;
            var sites = !string.IsNullOrWhiteSpace(Str(m, "tacticalDescription"));
            var practice = path!.Contains("/HURM/", StringComparison.OrdinalIgnoreCase) || path.Contains("Poveglia", StringComparison.OrdinalIgnoreCase)
                           || path.Contains("Skirmish", StringComparison.OrdinalIgnoreCase);
            maps.Add((code, name, sites && !practice));
        }
        return maps;
    }

    private static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
