namespace Esportra.Contracts.Auth;

/// <summary>
/// Identity carrier for requests authenticated via API key.
/// Stored in HttpContext.Items["ApiKeyContext"] by ApiKeyAuthMiddleware.
/// </summary>
public sealed record ApiKeyContext
{
    public Guid KeyId { get; init; }
    public Guid OrgId { get; init; }
    public Guid OwnerId { get; init; }
    public string[] Scopes { get; init; } = [];
    public string Environment { get; init; } = string.Empty;
    public int RateLimitPerMin { get; init; }
    public bool IsSandbox => Environment == "sandbox";
    public DateTimeOffset? GracePeriodUntil { get; init; }
    public string KeyPrefix { get; init; } = string.Empty;
}
