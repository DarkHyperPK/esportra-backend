using System.Text;
using System.Text.Json;

namespace Esportra.Api.Hubs;

/// <summary>
/// Server-side security guards applied to every <see cref="BroadcastEventPayload"/>
/// before it is persisted.  All guards are pure static methods so they can be
/// called identically from the SignalR hub and from the HTTP drain endpoints.
/// </summary>
public static class BroadcastEventGuard
{
    /// <summary>
    /// Complete set of event types the backend will accept from a node.
    /// Any event_type not in this set is silently dropped and logged as a warning.
    /// </summary>
    public static readonly HashSet<string> AllowedEventTypes = new(StringComparer.Ordinal)
    {
        "SegmentTake",
        "SegmentDone",
        "SponsorImpressionStarted",
        "SponsorImpressionEnded",
        "MatchResultSubmitted",
        "ShowStarted",
        "ShowEnded",
        "RoundStarted",
        "RoundEnded",
        "ProductionAction",
        "ProductionIncident",
    };

    /// <summary>Maximum JSON-encoded payload size in bytes.</summary>
    public const int MaxPayloadBytes = 8192;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="eventType"/> is on the allowlist.
    /// </summary>
    public static bool IsAllowedEventType(string eventType) =>
        AllowedEventTypes.Contains(eventType);

    public static bool IsValidTypedMetadata(string eventType, JsonElement? data)
    {
        if (eventType is "ProductionAction" or "ProductionIncident") return IsValidProductionMetadata(eventType, data);
        if (eventType is not ("RoundStarted" or "RoundEnded")) return true;
        if (data is not { ValueKind: JsonValueKind.Object } value) return false;
        var fields = new HashSet<string>(["segmentId", "segmentName", "segmentType", "wallClockMs"]);
        if (value.EnumerateObject().Any(property => !fields.Remove(property.Name)) || fields.Count != 0) return false;
        return value.GetProperty("segmentId") is { ValueKind: JsonValueKind.String } id && id.GetString() is { Length: <= 100 } key &&
            System.Text.RegularExpressions.Regex.IsMatch(key, "^round:[0-9a-fA-F-]{36}:[A-Z0-9_-]{1,24}:[1-9][0-9]{0,2}$") && Guid.TryParse(key.Split(':')[1], out _) && int.Parse(key.Split(':')[3]) <= 500 &&
            value.GetProperty("segmentName") is { ValueKind: JsonValueKind.String } name && name.GetString() is { Length: > 0 and <= 100 } label && !label.Any(char.IsControl) &&
            value.GetProperty("segmentType").ValueKind == JsonValueKind.String && value.GetProperty("segmentType").GetString() == "round" &&
            value.GetProperty("wallClockMs").ValueKind == JsonValueKind.Number && value.GetProperty("wallClockMs").TryGetInt64(out var stamp) && stamp is >= 0 and <= 253402300799999;
    }

    private static bool IsValidProductionMetadata(string eventType, JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } value) return false;
        var allowed = new HashSet<string>(["action", "kind", "status", "actorKind", "cause", "wallClockMs", "outputId"]);
        if (value.EnumerateObject().Any(property => !allowed.Remove(property.Name))) return false;
        string? Field(string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        if (Field("actorKind") is not ("operator" or "automation" or "observer" or "game-feed" or "system") ||
            Field("cause") is not ("manual" or "trigger" or "hold_expired" or "connection" or "missing_events") ||
            !value.TryGetProperty("wallClockMs", out var clock) || clock.ValueKind != JsonValueKind.Number ||
            !clock.TryGetInt64(out var stamp) || stamp is < 0 or > 253402300799999) return false;
        if (eventType == "ProductionAction")
            return value.EnumerateObject().Count() == 4 && (Field("action") is "hold" or "skip" or "take_requested" or "manual_take" or "resume" or "technical_pause" or "timeout_started" or "timeout_ended" or "data_fixed" or "data_fix_queued");
        return (Field("kind") is "obs_drop" or "stale_feed" or "output_drop") &&
            (Field("status") is "opened" or "resolved") && !value.TryGetProperty("action", out _) &&
            (!value.TryGetProperty("outputId", out _) || Guid.TryParse(Field("outputId"), out var outputId) && outputId != Guid.Empty);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="eventId"/> can be parsed as a UUID.
    /// The value is stored as TEXT in the DB but must be a valid GUID to prevent
    /// injection via the unique-conflict key.
    /// </summary>
    public static bool IsValidEventId(string? eventId) =>
        !string.IsNullOrWhiteSpace(eventId) && Guid.TryParse(eventId, out _);

    /// <summary>
    /// Serialises <paramref name="payload"/> to JSON, writes the result to
    /// <paramref name="serialized"/>, and returns <c>true</c> if the UTF-8
    /// byte count is within <see cref="MaxPayloadBytes"/>.
    /// </summary>
    public static bool IsPayloadWithinSizeLimit(BroadcastEventPayload payload, out string serialized)
    {
        serialized = JsonSerializer.Serialize(payload);
        return Encoding.UTF8.GetByteCount(serialized) <= MaxPayloadBytes;
    }

    /// <summary>
    /// Serialises a <see cref="JsonElement"/> data blob and returns its UTF-8 byte count.
    /// Used by the drain endpoints to check individual event data fields.
    /// </summary>
    public static bool IsDataWithinSizeLimit(JsonElement? data, out string serialized)
    {
        serialized = data.HasValue ? JsonSerializer.Serialize(data.Value) : "{}";
        return Encoding.UTF8.GetByteCount(serialized) <= MaxPayloadBytes;
    }
}
