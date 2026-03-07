namespace Esportra.Contracts.Requests;

public sealed record ProcessMatchResultRequest(string ReportId);
public sealed record ScanRecentMatchesRequest(string MatchId, string MapName, int GameNumber);
public sealed record AdvanceBracketRequest(string MatchId, string EventId);
public sealed record RecordMetricRequest(
    string SponsorId,
    string EventType,
    string PageUrl);
