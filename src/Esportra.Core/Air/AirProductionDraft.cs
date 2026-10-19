using System.Text.Json;

namespace Esportra.Core.Air;

/// <summary>Broadcast-only settings; no tournament identity or score mutations.</summary>
public static class AirProductionDraft
{
    private static readonly HashSet<string> Fields = ["sponsors", "talent", "crew"];
    private static readonly HashSet<string> PersonFields = ["name", "role", "logoUrl", "assetId"];

    public static string? Validate(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object || document.GetRawText().Length > 65536)
            return "Use a production settings object under 64 KB.";
        foreach (var property in document.EnumerateObject())
        {
            if (!Fields.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() > 50)
                return "Use up to 50 sponsors, talent or crew entries.";
            foreach (var row in property.Value.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("name", out var name) || !Plain(name, 120) || string.IsNullOrWhiteSpace(name.GetString()))
                    return "Every production entry needs a plain name.";
                foreach (var field in row.EnumerateObject())
                {
                    if (!PersonFields.Contains(field.Name) || !Plain(field.Value, field.Name == "logoUrl" ? 2048 : 120))
                        return "Use valid production names, responsibilities and logo URLs.";
                    if (field.Name == "assetId" && (!Guid.TryParse(field.Value.GetString(), out var assetId) || assetId == Guid.Empty))
                        return "Choose a valid uploaded logo.";
                    if (field.Name == "logoUrl" && field.Value.GetString() is { Length: > 0 } value &&
                        (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0))
                        return "Use a public HTTPS logo without credentials or signed query parameters.";
                }
            }
        }
        return null;
    }

    private static bool Plain(JsonElement value, int limit) => value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text && text.Length <= limit && !text.Any(c => char.IsControl(c) || c is '<' or '>');
}
