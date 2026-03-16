namespace Esportra.Contracts.Requests;

public sealed record RiotProxyRequest(string Region, string Endpoint);
public sealed record FaceitProxyRequest(string Endpoint);
