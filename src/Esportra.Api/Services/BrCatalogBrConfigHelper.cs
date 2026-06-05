using System.Text.Json;
using System.Text.Json.Nodes;

namespace Esportra.Api.Services;

/// <summary>
/// Reads and enriches Battle Royale config from the game catalog <c>br_config</c> JSON.
/// </summary>
public static class BrCatalogBrConfigHelper
{
    public const string MapPlaceholderBase = "https://placehold.co/640x360/1a1a2e/eaeaea?text=";

    public static object EnrichBrConfigForApi(string? brConfigJson)
    {
        if (string.IsNullOrWhiteSpace(brConfigJson))
            return new Dictionary<string, object?>();

        try
        {
            var node = JsonNode.Parse(brConfigJson) as JsonObject;
            if (node is null)
                return JsonSerializer.Deserialize<JsonElement>(brConfigJson)!;

            if (node["maps"] is JsonObject mapsNode)
            {
                var items = BuildMapItems(mapsNode);
                if (items.Count > 0)
                    mapsNode["items"] = items;
            }

            return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString())!;
        }
        catch
        {
            return JsonSerializer.Deserialize<JsonElement>(brConfigJson)!;
        }
    }

    public static string BuildMapPlaceholderUrl(string mapName) =>
        $"{MapPlaceholderBase}{Uri.EscapeDataString(mapName)}";

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

    public static BattleRoyaleConfigResolver.BrMapMode? ReadDefaultMapMode(object? catalogBrConfig)
    {
        if (!TryParseJsonElement(catalogBrConfig, out var root))
            return null;

        if (TryGetPropertyIgnoreCase(root, "defaultMapMode", out var modeEl) && modeEl.ValueKind == JsonValueKind.String)
            return BattleRoyaleConfigResolver.ParseMapModePublic(modeEl.GetString());

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

    private static JsonArray BuildMapItems(JsonObject mapsNode)
    {
        var items = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (mapsNode["items"] is JsonArray existingItems)
        {
            foreach (var item in existingItems)
            {
                if (item is not JsonObject itemObj)
                    continue;

                var name = itemObj["name"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                    continue;

                var imageUrl = itemObj["imageUrl"]?.GetValue<string>()?.Trim();
                items.Add(new JsonObject
                {
                    ["name"] = name,
                    ["imageUrl"] = string.IsNullOrWhiteSpace(imageUrl) ? BuildMapPlaceholderUrl(name) : imageUrl,
                });
            }

            if (items.Count > 0)
                return items;
        }

        if (mapsNode["pool"] is JsonArray pool)
        {
            foreach (var entry in pool)
            {
                var name = entry?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                    continue;

                items.Add(new JsonObject
                {
                    ["name"] = name,
                    ["imageUrl"] = BuildMapPlaceholderUrl(name),
                });
            }
        }

        return items;
    }

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

    private static bool TryParseJsonElement(object? rawValue, out JsonElement element)
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

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
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
