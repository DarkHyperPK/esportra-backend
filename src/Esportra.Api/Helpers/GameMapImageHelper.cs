using Esportra.Core.Games;

namespace Esportra.Api.Helpers;

public sealed class GameMapRow
{
    public string id { get; set; } = "";
    public string game { get; set; } = "";
    public string map_name { get; set; } = "";
    public string? map_image_url { get; set; }
    public bool is_active { get; set; }
}

public static class GameMapImageHelper
{
    public static void ApplyR6Fallback(GameMapRow map, string? supabaseUrl) =>
        map.map_image_url = R6MapCatalog.ResolveImageUrl(
            map.game,
            map.map_name,
            map.map_image_url,
            supabaseUrl);

    public static IEnumerable<GameMapRow> EnrichRows(IEnumerable<GameMapRow> maps, string? supabaseUrl)
    {
        foreach (var map in maps)
        {
            ApplyR6Fallback(map, supabaseUrl);
            yield return map;
        }
    }
}
