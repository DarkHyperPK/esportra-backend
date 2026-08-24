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
        "proposal_data", "match", "match_dispute", "reports", "riot_accounts",
        "data", "evidence_urls", "media", "details", "filters",
        "default_value", "conditions", "value"
    };

    public static void FixJsonb(IEnumerable<dynamic> rows)
    {
        foreach (var row in rows) FixJsonb(row);
    }

    public static void FixJsonb(dynamic row)
    {
        if (row is not IDictionary<string, object?> d) return;

        foreach (var key in JsonbColumns)
        {
            if (!d.TryGetValue(key, out var v) || v is not string str || str.Length == 0) continue;
            try
            {
                var element = JsonSerializer.Deserialize<JsonElement>(str);
                d[key] = element;
                if (key.Equals("match_dispute", StringComparison.OrdinalIgnoreCase))
                    FixNestedEvidenceUrls(element, d, key);
            }
            catch { /* keep raw string */ }
        }
    }

    private static void FixNestedEvidenceUrls(JsonElement element, IDictionary<string, object?> row, string key)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        if (!element.TryGetProperty("evidence_urls", out var urls)) return;
        if (urls.ValueKind is JsonValueKind.Array or JsonValueKind.Null) return;

        var clone = element.Deserialize<Dictionary<string, JsonElement>>();
        if (clone is null) return;

        if (urls.ValueKind == JsonValueKind.String)
        {
            var raw = urls.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try { clone["evidence_urls"] = JsonSerializer.Deserialize<JsonElement>(raw); }
                catch
                {
                    clone["evidence_urls"] = JsonSerializer.SerializeToElement(
                        raw.Trim('{', '}').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
        }

        row[key] = JsonSerializer.SerializeToElement(clone);
    }
}
