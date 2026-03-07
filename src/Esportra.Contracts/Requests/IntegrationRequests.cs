namespace Esportra.Contracts.Requests;

public sealed record RiotProxyRequest(string Region, string Endpoint);
public sealed record FaceitProxyRequest(string Endpoint);

public sealed record RiotOAuthCallbackRequest(
    string Code,
    string RedirectUri);

public sealed record FaceitOAuthCallbackRequest(
    string Code,
    string CodeVerifier,
    string RedirectUri);
