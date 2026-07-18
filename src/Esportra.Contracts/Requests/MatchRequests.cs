namespace Esportra.Contracts.Requests;

public sealed record ProcessMatchResultRequest(Guid ReportId);
public sealed record ScanRecentMatchesRequest(Guid MatchId, string MapName, int GameNumber);
public sealed record AdvanceBracketRequest(Guid MatchId, Guid? EventId);
