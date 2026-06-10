using System.Globalization;

namespace Esportra.Api.Helpers;

public static class ProfileResponseNormalizer
{
    public static Dictionary<string, object?>? ToDictionary(object? row)
    {
        if (row is null) return null;

        if (row is IDictionary<string, object?> typedDict)
        {
            return typedDict.ToDictionary(
                static pair => pair.Key,
                static pair => NormalizeValue(pair.Value));
        }

        if (row is IDictionary<string, object> legacyDict)
        {
            return legacyDict.ToDictionary(
                static pair => pair.Key,
                static pair => NormalizeValue(pair.Value));
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
}
