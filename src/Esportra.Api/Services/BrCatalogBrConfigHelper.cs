using System.Text.Json;
using System.Text.Json.Nodes;
using Esportra.Core.Br;

namespace Esportra.Api.Services;

/// <summary>
/// API-layer enrichment for game catalog BR config. Pure readers live in <see cref="BrCatalogConfigReader"/>.
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

    public static IReadOnlyList<string> ReadMapPool(object? catalogBrConfig) =>
        BrCatalogConfigReader.ReadMapPool(catalogBrConfig);

    public static bool ReadHasMaps(object? catalogBrConfig, IReadOnlyList<string> resolvedPool) =>
        BrCatalogConfigReader.ReadHasMaps(catalogBrConfig, resolvedPool);

    public static BrMapMode? ReadDefaultMapMode(object? catalogBrConfig) =>
        BrCatalogConfigReader.ReadDefaultMapMode(catalogBrConfig);

    public static string? ReadDefaultPreset(object? catalogBrConfig) =>
        BrCatalogConfigReader.ReadDefaultPreset(catalogBrConfig);

    public static int ReadPlayersPerLobby(object? catalogBrConfig, int fallback = 100) =>
        BrCatalogConfigReader.ReadPlayersPerLobby(catalogBrConfig, fallback);

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
}
