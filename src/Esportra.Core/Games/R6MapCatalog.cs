namespace Esportra.Core.Games;

/// <summary>
/// Canonical Rainbow Six Siege map metadata for storage paths and image URL resolution.
/// </summary>
public static class R6MapCatalog
{
    public const string GameName = "Rainbow Six Siege";
    public const string StorageBucket = "system.assets.games";
    public const string StoragePrefix = "r6/maps";

    private static readonly Dictionary<string, string> DisplayNameToSlug =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Bank"] = "bank",
            ["Border"] = "border",
            ["Calypso Casino"] = "calypso-casino",
            ["Chalet"] = "chalet",
            ["Close Quarters"] = "close-quarters",
            ["Clubhouse"] = "clubhouse",
            ["Coastline"] = "coastline",
            ["Consulate"] = "consulate",
            ["Emerald Plains"] = "emerald-plains",
            ["Favela"] = "favela",
            ["Fortress"] = "fortress",
            ["Hereford"] = "hereford",
            ["House"] = "house",
            ["Kanal"] = "kanal",
            ["Kafe Dostoyevsky"] = "kafe-dostoyevsky",
            ["Lair"] = "lair",
            ["Nighthaven Labs"] = "nighthaven-labs",
            ["Oregon"] = "oregon",
            ["Outback"] = "outback",
            ["Plane"] = "plane",
            ["Skyscraper"] = "skyscraper",
            ["Stadium Alpha"] = "stadium-alpha",
            ["Stadium Bravo"] = "stadium-bravo",
            ["Theme Park"] = "theme-park",
            ["Tower"] = "tower",
            ["Villa"] = "villa",
            ["Yacht"] = "yacht",
        };

    private static readonly Dictionary<string, string> SlugToFilename =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["bank"] = "Bank.avif",
            ["border"] = "Border.avif",
            ["calypso-casino"] = "CalypsoCasino.avif",
            ["chalet"] = "Chalet.avif",
            ["close-quarters"] = "closequarters.avif",
            ["clubhouse"] = "ClubHouse.avif",
            ["coastline"] = "coastline.avif",
            ["consulate"] = "Consulate.avif",
            ["emerald-plains"] = "emeraldplains.avif",
            ["favela"] = "favela.avif",
            ["fortress"] = "fortress.avif",
            ["hereford"] = "hereford.avif",
            ["house"] = "house.avif",
            ["kanal"] = "kanal.avif",
            ["kafe-dostoyevsky"] = "RussianCafe.avif",
            ["lair"] = "Lair.avif",
            ["nighthaven-labs"] = "Nighthaven.avif",
            ["oregon"] = "oregon.avif",
            ["outback"] = "outback.avif",
            ["plane"] = "plane.avif",
            ["skyscraper"] = "skycraper.avif",
            ["stadium-alpha"] = "StadiumA.avif",
            ["stadium-bravo"] = "stadiumB.avif",
            ["theme-park"] = "themepark.avif",
            ["tower"] = "tower.avif",
            ["villa"] = "villa.avif",
            ["yacht"] = "yacht.avif",
        };

    public static bool IsRainbowSix(string? game) =>
        !string.IsNullOrWhiteSpace(game)
        && game.Contains("rainbow six", StringComparison.OrdinalIgnoreCase);

    public static bool TryGetSlug(string mapName, out string slug) =>
        DisplayNameToSlug.TryGetValue(mapName.Trim(), out slug!);

    public static bool TryGetSourceFilename(string slug, out string filename) =>
        SlugToFilename.TryGetValue(slug, out filename!);

    public static string BuildObjectPath(string slug) => $"{StoragePrefix}/{slug}.avif";

    public static string? BuildPublicUrl(string? supabaseUrl, string slug)
    {
        if (string.IsNullOrWhiteSpace(supabaseUrl))
            return null;

        return $"{supabaseUrl.TrimEnd('/')}/storage/v1/object/public/{StorageBucket}/{BuildObjectPath(slug)}";
    }

    public static string? ResolveImageUrl(string? game, string? mapName, string? existingUrl, string? supabaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(existingUrl))
            return existingUrl.Trim();

        if (!IsRainbowSix(game) || string.IsNullOrWhiteSpace(mapName))
            return existingUrl;

        return TryGetSlug(mapName, out var slug)
            ? BuildPublicUrl(supabaseUrl, slug)
            : existingUrl;
    }

    public static IReadOnlyList<(string DisplayName, string Slug, string Filename)> GetSeedEntries() =>
        DisplayNameToSlug
            .Select(pair =>
            {
                var slug = pair.Value;
                if (!SlugToFilename.TryGetValue(slug, out var filename))
                    return default;

                return (DisplayName: pair.Key, Slug: slug, Filename: filename);
            })
            .Where(entry => entry != default)
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
