namespace Esportra.Contracts.Requests;

public sealed record RiotProxyRequest(string Region, string Endpoint);

public sealed record RiotEnrichedMatchRequest(string Region, string MatchId);
