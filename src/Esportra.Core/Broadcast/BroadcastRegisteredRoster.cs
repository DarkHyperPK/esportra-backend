using System.Text.Json;

namespace Esportra.Core.Broadcast;

public sealed record BroadcastRosterName(string Name, string Role, Guid? PlayerId = null, string? InGameName = null, string? GameId = null);
public static class BroadcastRegisteredRoster
{
    public static BroadcastRosterName[] Read(string? lineup, string? legacy)
    {
        var names = new List<BroadcastRosterName>();
        try
        {
            using var document = JsonDocument.Parse(lineup ?? "{}");
            foreach (var (field, role) in new[] { ("starters", "starter"), ("substitutes", "substitute"), ("coaches", "coach") })
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(field, out var rows) && rows.ValueKind == JsonValueKind.Array)
                    foreach (var row in rows.EnumerateArray().Take(10))
                        if (row.ValueKind == JsonValueKind.Object && (row.TryGetProperty("displayName", out var name) || row.TryGetProperty("DisplayName", out name)) && name.ValueKind == JsonValueKind.String)
                        {
                            Guid? id = (row.TryGetProperty("userId", out var user) || row.TryGetProperty("UserId", out user)) && user.ValueKind == JsonValueKind.String && Guid.TryParse(user.GetString(), out var parsed) ? parsed : null;
                            Add(names, name.GetString(), role, id);
                        }
        }
        catch (JsonException) { }
        if (names.Count == 0)
            try
            {
                using var document = JsonDocument.Parse(legacy ?? "[]");
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var name in document.RootElement.EnumerateArray().Take(10))
                        if (name.ValueKind == JsonValueKind.String) Add(names, name.GetString(), "registered");
            }
            catch (JsonException) { }
        return names.Take(10).ToArray();
    }

    private static void Add(List<BroadcastRosterName> names, string? value, string role, Guid? playerId = null)
    {
        var name = value?.Trim();
        if (!string.IsNullOrEmpty(name) && name.Length <= 80 && !name.Any(char.IsControl)) names.Add(new(name, role, playerId));
    }
}
