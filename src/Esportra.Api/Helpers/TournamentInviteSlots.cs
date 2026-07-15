using System.Text.Json;

namespace Esportra.Api.Helpers;

/// <summary>
/// Resolves reserved invite slot counts from the tournaments column and/or settings JSON.
/// Wizard and dashboard may write settings before the column is synced; reads must check both.
/// </summary>
public static class TournamentInviteSlots
{
    public static int ResolveForWrite(int? requestedColumn, object? settings)
    {
        var requested = Math.Max(requestedColumn ?? 0, 0);
        if (requested > 0) return requested;
        return TryReadFromSettings(settings);
    }

    public static int ResolveFromRow(dynamic row)
    {
        var column = ReadColumn(row);
        object? settings = null;
        try
        {
            if (row is IDictionary<string, object?> dict && dict.TryGetValue("settings", out var s))
                settings = s;
            else
                settings = row.settings;
        }
        catch
        {
            // ignore dynamic access failures
        }

        return Resolve(column, settings);
    }

    public static int Resolve(int column, object? settings)
    {
        if (column > 0) return column;
        return TryReadFromSettings(settings);
    }

    public static int TryReadFromSettings(object? settings)
    {
        return TryReadFromSettingsIfPresent(settings, out var value) ? value : 0;
    }

    public static bool TryReadFromSettingsIfPresent(object? settings, out int value)
    {
        value = 0;
        if (settings is null) return false;

        try
        {
            var element = settings switch
            {
                JsonElement jsonElement => jsonElement,
                string json when !string.IsNullOrWhiteSpace(json) => JsonSerializer.Deserialize<JsonElement>(json),
                _ => JsonSerializer.SerializeToElement(settings),
            };

            if (element.ValueKind != JsonValueKind.Object) return false;

            if (element.TryGetProperty("reservedInviteSlots", out var camel) && TryReadInt(camel, out var camelValue))
            {
                value = Math.Max(camelValue, 0);
                return true;
            }

            if (element.TryGetProperty("reserved_invite_slots", out var snake) && TryReadInt(snake, out var snakeValue))
            {
                value = Math.Max(snakeValue, 0);
                return true;
            }
        }
        catch
        {
            // ignore malformed settings payloads
        }

        return false;
    }

    private static int ReadColumn(dynamic row)
    {
        try
        {
            return Math.Max(Convert.ToInt32(row.reserved_invite_slots ?? 0), 0);
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
            return true;

        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), out value))
            return true;

        value = 0;
        return false;
    }
}
