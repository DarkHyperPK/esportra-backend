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
    };

    /// <summary>Maximum JSON-encoded payload size in bytes.</summary>
    public const int MaxPayloadBytes = 8192;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="eventType"/> is on the allowlist.
    /// </summary>
    public static bool IsAllowedEventType(string eventType) =>
        AllowedEventTypes.Contains(eventType);

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
