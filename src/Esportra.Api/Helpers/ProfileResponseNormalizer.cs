using System.Globalization;
using System.Text.Json;

namespace Esportra.Api.Helpers;

public static class ProfileResponseNormalizer
{
    private const string DiceBearBase = "https://api.dicebear.com/10.x";
    private const string DefaultStyle = "critters";

    // JSONB columns that Dapper returns as raw JSON strings and need to be parsed back to objects.
    private static readonly HashSet<string> JsonbColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "social_links", "privacy_settings", "notification_preferences",
    };

    public static Dictionary<string, object?>? ToDictionary(object? row)
    {
        if (row is null) return null;

        if (row is IDictionary<string, object?> typedDict)
        {
            var result = typedDict.ToDictionary(
                static pair => pair.Key,
                pair => NormalizeValue(pair.Key, pair.Value));
            InjectDiceBearAvatarUrl(result);
            return result;
        }

        if (row is IDictionary<string, object> legacyDict)
        {
            var result = legacyDict.ToDictionary(
                static pair => pair.Key,
                pair => NormalizeValue(pair.Key, pair.Value));
            InjectDiceBearAvatarUrl(result);
            return result;
        }

        return null;
    }

    public static object? NormalizeValue(string key, object? value)
    {
        if (value is string raw && JsonbColumns.Contains(key) && raw.Length > 0)
        {
            try { return JsonSerializer.Deserialize<JsonElement>(raw); }
            catch { /* leave as string if parse fails */ }
        }
        return NormalizeValue(value);
    }

    public static object? NormalizeValue(object? value) => value switch
    {
        null => null,
        DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => value,
    };

    // Compute a DiceBear URL only when avatar_seed is explicitly set by the user.
    // No seed → leave avatar_url null so the frontend renders its own placeholder.
    private static void InjectDiceBearAvatarUrl(Dictionary<string, object?> dict)
    {
        var avatarUrl = dict.GetValueOrDefault("avatar_url") as string;
        if (!string.IsNullOrWhiteSpace(avatarUrl)) return;

        var seed = dict.GetValueOrDefault("avatar_seed") as string;
        if (string.IsNullOrWhiteSpace(seed)) return;

        var style = dict.GetValueOrDefault("avatar_style") as string;
        if (string.IsNullOrWhiteSpace(style)) style = DefaultStyle;

        dict["avatar_url"] = $"{DiceBearBase}/{style}/svg?seed={Uri.EscapeDataString(seed)}";
    }
}
