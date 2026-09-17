using System.Globalization;

namespace Esportra.Api.Helpers;

public static class ProfileResponseNormalizer
{
    private const string DiceBearBaseUrl = "https://api.dicebear.com/10.x/critters/svg";

    public static Dictionary<string, object?>? ToDictionary(object? row)
    {
        if (row is null) return null;

        if (row is IDictionary<string, object?> typedDict)
        {
            var result = typedDict.ToDictionary(
                static pair => pair.Key,
                static pair => NormalizeValue(pair.Value));
            InjectDiceBearAvatarUrl(result);
            return result;
        }

        if (row is IDictionary<string, object> legacyDict)
        {
            var result = legacyDict.ToDictionary(
                static pair => pair.Key,
                static pair => NormalizeValue(pair.Value));
            InjectDiceBearAvatarUrl(result);
            return result;
        }

        return null;
    }

    public static object? NormalizeValue(object? value) => value switch
    {
        null => null,
        DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => value,
    };

    // When avatar_url is absent, compute a DiceBear critters URL from avatar_seed (or user id as fallback).
    private static void InjectDiceBearAvatarUrl(Dictionary<string, object?> dict)
    {
        var avatarUrl = dict.GetValueOrDefault("avatar_url") as string;
        if (!string.IsNullOrWhiteSpace(avatarUrl)) return;

        var seed = dict.GetValueOrDefault("avatar_seed") as string;
        if (string.IsNullOrWhiteSpace(seed))
            seed = dict.GetValueOrDefault("id") as string;

        if (!string.IsNullOrWhiteSpace(seed))
            dict["avatar_url"] = $"{DiceBearBaseUrl}?seed={Uri.EscapeDataString(seed)}";
    }
}
