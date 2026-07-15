using System.Text.Json;

namespace Esportra.Api.Services;

/// <summary>
/// Seed data used when importing or backfilling catalog banner URLs.
/// Not consulted at email send time — only during catalog writes.
/// </summary>
internal static class GameCatalogBannerSeed
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> BySlug = new(BuildBySlug);

    public static string? TryResolve(string slug, string? name = null)
    {
        if (!string.IsNullOrWhiteSpace(slug)
            && BySlug.Value.TryGetValue(NormalizeKey(slug), out var bySlug))
        {
            return bySlug;
        }

        if (!string.IsNullOrWhiteSpace(name)
            && BySlug.Value.TryGetValue(NormalizeKey(name), out var byName))
        {
            return byName;
        }

        return null;
    }

    public static string? NormalizeAbsoluteHttpsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? trimmed : null;
    }

    private static IReadOnlyDictionary<string, string> BuildBySlug()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "GameCatalog", "game-banner-seed.json");
        if (!File.Exists(manifestPath)) return map;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var heroUrl = entry.TryGetProperty("heroUrl", out var heroProp)
                    ? heroProp.GetString()
                    : null;
                var normalized = NormalizeAbsoluteHttpsUrl(heroUrl);
                if (normalized is null) continue;

                AddKey(map, entry, "slug", normalized);
                AddKey(map, entry, "name", normalized);
                AddKey(map, entry, "matchedGame", normalized);
            }
        }
        catch
        {
            // Import/backfill will simply leave banner_url null when seed is unavailable.
        }

        foreach (var (alias, canonical) in s_aliases)
        {
            if (map.TryGetValue(NormalizeKey(canonical), out var heroUrl))
                map[NormalizeKey(alias)] = heroUrl;
        }

        return map;
    }

    private static void AddKey(Dictionary<string, string> map, JsonElement entry, string property, string heroUrl)
    {
        if (!entry.TryGetProperty(property, out var valueProp)) return;
        var value = valueProp.GetString();
        if (string.IsNullOrWhiteSpace(value)) return;
        map[NormalizeKey(value)] = heroUrl;
    }

    private static string NormalizeKey(string value) => value.Trim().ToLowerInvariant();

    private static readonly (string Alias, string Canonical)[] s_aliases =
    [
        ("CS2", "Counter-Strike 2"),
        ("CSGO", "Counter-Strike 2"),
        ("Counter-Strike: Global Offensive", "Counter-Strike 2"),
        ("LoL", "League of Legends"),
        ("PUBG: Battlegrounds", "PUBG"),
        ("PlayerUnknown's Battlegrounds", "PUBG"),
        ("EA Sports FC", "EA FC"),
        ("EA Sports FC 25", "EA FC"),
        ("FIFA", "EA FC"),
        ("R6", "Rainbow Six Siege"),
        ("Warzone", "Call of Duty: Warzone"),
        ("CoD: Warzone", "Call of Duty: Warzone"),
    ];
}
