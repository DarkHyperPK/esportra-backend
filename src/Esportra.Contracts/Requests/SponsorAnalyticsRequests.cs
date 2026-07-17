namespace Esportra.Contracts.Requests;

public sealed record RecordSponsorAnalyticsEventRequest(
    Guid EventId,
    Guid SponsorId,
    string EventType,
    string Placement,
    Guid? TournamentId = null,
    string? PagePath = null,
    int SchemaVersion = 1);
