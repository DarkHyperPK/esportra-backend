using System.Text.Json;
using Esportra.Core.Tournaments;

namespace Esportra.Core.Tournaments;

public static class RosterLineupSubmissionParser
{
    public sealed record ParsedEntry(Guid UserId, string Role, string? DisplayName);

    public static IReadOnlyList<RosterLineupMember> ToValidatorMembers(IEnumerable<ParsedEntry> entries) =>
        entries.Select(entry => new RosterLineupMember(
            entry.UserId,
            entry.Role,
            entry.Role == "starter",
            null)).ToList();

    public static IReadOnlyList<ParsedEntry> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Tournament lineup is required.");

        var normalized = json.Trim();
        if (normalized.StartsWith('"') && normalized.EndsWith('"'))
        {
            try
            {
                normalized = JsonSerializer.Deserialize<string>(normalized)
                    ?? throw new InvalidOperationException("Tournament lineup payload is invalid.");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("Tournament lineup payload is invalid.");
            }
        }

        using var doc = JsonDocument.Parse(normalized);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Tournament lineup payload is invalid.");

        var entries = new List<ParsedEntry>();
        AppendRoleGroup(root, "starters", "starter", entries);
        AppendRoleGroup(root, "substitutes", "substitute", entries);
        AppendRoleGroup(root, "coaches", "coach", entries);

        if (entries.Count == 0)
            throw new InvalidOperationException("Tournament lineup must include at least one player.");

        return entries;
    }

    private static void AppendRoleGroup(
        JsonElement root,
        string propertyName,
        string role,
        ICollection<ParsedEntry> entries)
    {
        if (!root.TryGetProperty(propertyName, out var group) || group.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in group.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Tournament lineup entry is invalid.");

            if (!TryReadUserId(item, out var userId))
                throw new InvalidOperationException("Tournament lineup entry is missing userId.");

            var displayName = item.TryGetProperty("displayName", out var displayEl) && displayEl.ValueKind == JsonValueKind.String
                ? displayEl.GetString()
                : item.TryGetProperty("display_name", out var snakeEl) && snakeEl.ValueKind == JsonValueKind.String
                    ? snakeEl.GetString()
                    : null;

            entries.Add(new ParsedEntry(userId, role, displayName));
        }
    }

    private static bool TryReadUserId(JsonElement item, out Guid userId)
    {
        userId = Guid.Empty;
        if (item.TryGetProperty("userId", out var camel) && camel.ValueKind == JsonValueKind.String
            && Guid.TryParse(camel.GetString(), out userId))
            return true;

        if (item.TryGetProperty("user_id", out var snake) && snake.ValueKind == JsonValueKind.String
            && Guid.TryParse(snake.GetString(), out userId))
            return true;

        return false;
    }
}
