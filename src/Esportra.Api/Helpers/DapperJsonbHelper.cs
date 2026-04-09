using System.Text.Json;

namespace Esportra.Api.Helpers;

/// <summary>
/// Dapper returns PostgreSQL jsonb columns as raw strings when mapping to dynamic.
/// System.Text.Json then double-encodes them as JSON string values.
/// This helper deserializes those strings into JsonElement so they serialize correctly.
/// </summary>
public static class DapperJsonbHelper
{
    private static readonly HashSet<string> JsonbColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "match_data", "screenshot_urls", "metadata", "match_details",
        "proposal_data", "match", "data", "evidence_urls", "media",
        "details", "filters"
    };

    public static void FixJsonb(IEnumerable<dynamic> rows)
    {
        foreach (var row in rows) FixJsonb(row);
    }

    public static void FixJsonb(dynamic row)
    {
        if (row is IDictionary<string, object?> d)
        {
            foreach (var key in JsonbColumns)
                if (d.TryGetValue(key, out var v) && v is string str && str.Length > 0)
                    try { d[key] = JsonSerializer.Deserialize<JsonElement>(str); } catch { }
        }
    }
}
