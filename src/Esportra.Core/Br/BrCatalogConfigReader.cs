using System.Text.Json;

namespace Esportra.Core.Br;

/// <summary>
/// Canonical BR config resolver — single source of truth (frontend reads via API).
/// </summary>
public static class BrCatalogConfigReader
{
    public static IReadOnlyList<string> ReadMapPool(object? catalogBrConfig)
    {
        if (!TryParseJsonElement(catalogBrConfig, out var root))
            return [];

        if (!TryGetPropertyIgnoreCase(root, "maps", out var mapsEl) || mapsEl.ValueKind != JsonValueKind.Object)
            return [];

        var fromItems = ReadMapNamesFromItems(mapsEl);
        if (fromItems.Count > 0)
            return fromItems;

        return ReadStringArray(mapsEl, "pool");
    }

    public static bool ReadHasMaps(object? catalogBrConfig, IReadOnlyList<string> resolvedPool)
    {
        if (!TryParseJsonElement(catalogBrConfig, out var root))
            return resolvedPool.Count > 0;

        if (TryGetPropertyIgnoreCase(root, "maps", out var mapsEl)
            && mapsEl.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(mapsEl, "hasMaps", out var hasMapsEl)
            && (hasMapsEl.ValueKind == JsonValueKind.True || hasMapsEl.ValueKind == JsonValueKind.False))
        {
            return hasMapsEl.GetBoolean();
        }

        return resolvedPool.Count > 0;
    }

    public static BrMapMode? ReadDefaultMapMode(object? catalogBrConfig)
    {
        if (!TryParseJsonElement(catalogBrConfig, out var root))
            return null;

        if (TryGetPropertyIgnoreCase(root, "defaultMapMode", out var modeEl) && modeEl.ValueKind == JsonValueKind.String)
            return ParseMapMode(modeEl.GetString());

        return null;
    }

    public static string? ReadDefaultPreset(object? catalogBrConfig)
    {
        if (!TryParseJsonElement(catalogBrConfig, out var root))
            return null;

        if (TryGetPropertyIgnoreCase(root, "defaultPreset", out var presetEl) && presetEl.ValueKind == JsonValueKind.String)
            return presetEl.GetString();

        return null;
    }

    public static int ReadPlayersPerLobby(object? catalogBrConfig, int fallback = 100)
    {
        if (!TryParseJsonElement(catalogBrConfig, out var root))
            return fallback;

        if (TryGetPropertyIgnoreCase(root, "playersPerLobby", out var playersEl)
            && playersEl.TryGetInt32(out var players)
            && players > 0)
        {
            return players;
        }

        return fallback;
    }

    public static BrMapMode? ParseMapMode(string? rawMode) =>
        rawMode?.Trim().ToLowerInvariant() switch
        {
            "none" => BrMapMode.None,
            "fixed_stage" => BrMapMode.FixedStage,
            "per_round" => BrMapMode.PerRound,
            "rotation" => BrMapMode.Rotation,
            _ => null,
        };

    private static List<string> ReadMapNamesFromItems(JsonElement mapsEl)
    {
        var names = new List<string>();
        if (!TryGetPropertyIgnoreCase(mapsEl, "items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            return names;

        foreach (var item in itemsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (TryGetPropertyIgnoreCase(item, "name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                var name = nameEl.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }

        return names;
    }

    private static List<string> ReadStringArray(JsonElement element, string propertyName)
    {
        var values = new List<string>();
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var arrayEl) || arrayEl.ValueKind != JsonValueKind.Array)
            return values;

        foreach (var item in arrayEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value);
            }
        }

        return values;
    }

    internal static bool TryParseJsonElement(object? rawValue, out JsonElement element)
    {
        switch (rawValue)
        {
            case JsonElement jsonElement:
                element = jsonElement.Clone();
                return true;
            case JsonDocument jsonDocument:
                element = jsonDocument.RootElement.Clone();
                return true;
            case string jsonText when !string.IsNullOrWhiteSpace(jsonText):
                try
                {
                    using var parsed = JsonDocument.Parse(jsonText);
                    element = parsed.RootElement.Clone();
                    return true;
                }
                catch
                {
                    element = default;
                    return false;
                }
        }

        element = default;
        return false;
    }

    internal static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }
}
