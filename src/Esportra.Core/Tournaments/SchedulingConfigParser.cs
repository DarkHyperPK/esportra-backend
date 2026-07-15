using System.Text.Json;

namespace Esportra.Core.Tournaments;

/// <summary>
/// Parses tournament_stages.scheduling_config JSONB with tolerant key casing.
/// </summary>
public static class SchedulingConfigParser
{
    public static SchedulingConfigSnapshot Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return SchedulingConfigSnapshot.Default;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            return SchedulingConfigSnapshot.Default;
        }
    }

    public static SchedulingConfigSnapshot Parse(JsonElement root)
    {
        var selfPlayEnabled = ReadBool(root, "self_play_enabled", "selfPlayEnabled");
        var checkinWindowMinutes = ReadInt(root, 15,
            "checkin_window_minutes", "checkinWindowMinutes", "CheckinWindowMinutes");
        var roundDeadlines = ReadStringDictionary(root, "round_deadlines", "roundDeadlines");

        return new SchedulingConfigSnapshot(
            SelfPlayEnabled: selfPlayEnabled,
            CheckinWindowMinutes: checkinWindowMinutes,
            RoundDeadlines: roundDeadlines);
    }

    private static bool ReadBool(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var value))
                continue;

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
                JsonValueKind.Number => value.TryGetInt32(out var n) && n != 0,
                _ => false,
            };
        }

        return false;
    }

    private static int ReadInt(JsonElement root, int fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
                return n > 0 ? n : fallback;

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), out var parsed)
                && parsed > 0)
                return parsed;
        }

        return fallback;
    }

    private static IReadOnlyDictionary<string, string> ReadStringDictionary(
        JsonElement root,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Object)
                continue;

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var text = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(text))
                    dict[property.Name] = text;
            }

            return dict;
        }

        return new Dictionary<string, string>();
    }
}
