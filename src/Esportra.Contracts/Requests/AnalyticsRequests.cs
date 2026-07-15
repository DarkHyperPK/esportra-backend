namespace Esportra.Contracts.Requests;

public sealed record TrackEventRequest(
    string EventType,
    string? EventData = "{}",
    string? SessionId = null,
    string? UserAgent = null);
