using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dapper;

namespace Esportra.Api.Features;

/// <summary>
/// Blocks API surfaces of platform features that are switched off in the Admin Centre.
/// Returns a structured 409 envelope — never data loss; re-enabling restores access.
/// Enabled-state is cached briefly (30s) so toggles take effect quickly without per-request queries.
/// </summary>
public sealed class FeatureGateMiddleware(
    RequestDelegate next,
    IDbConnectionFactory connectionFactory,
    ILogger<FeatureGateMiddleware> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    // (feature key, path regex) — first match wins. Keep in sync with FeatureCatalog.
    private static readonly (string Key, Regex Pattern)[] Rules =
    [
        ("battle-royale", new Regex(@"^/api/br/", RegexOptions.Compiled)),
        ("tournament-registration", new Regex(@"^/api/tournaments/[^/]+/register$", RegexOptions.Compiled)),
        ("team-invites", new Regex(@"^/api/teams/invites/|^/api/teams/[^/]+(/rosters/[^/]+)?/invite(-batch)?$", RegexOptions.Compiled)),
        ("wallet-pos", new Regex(@"^/api/(venues/[^/]+/(wallets|pos)|wallets/my)", RegexOptions.Compiled)),
        ("sponsor-ads", new Regex(@"^/api/sponsor-analytics/events$|^/api/sponsors/active$", RegexOptions.Compiled)),
        ("broadcasts", new Regex(@"^/api/notifications/broadcasts|^/api/admin/broadcasts/[^/]+/send$", RegexOptions.Compiled)),
        ("ghost-mode", new Regex(@"^/api/admin/ghost", RegexOptions.Compiled)),
    ];

    private readonly ConcurrentDictionary<string, (bool Enabled, DateTime ExpiresAt)> _cache = new();

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method == HttpMethods.Options || !context.Request.Path.HasValue)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value!;
        string? matchedFeature = null;
        foreach (var (key, pattern) in Rules)
        {
            if (pattern.IsMatch(path))
            {
                matchedFeature = key;
                break;
            }
        }

        if (matchedFeature is null)
        {
            await next(context);
            return;
        }

        var enabled = await IsEnabledAsync(matchedFeature, context.RequestAborted);
        if (enabled)
        {
            await next(context);
            return;
        }

        logger.LogInformation("Blocked {Method} {Path} — feature '{Feature}' is disabled",
            context.Request.Method, path, matchedFeature);

        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "feature_disabled",
            feature = matchedFeature,
            message = "This feature is currently turned off by platform administrators."
        });
    }

    private async Task<bool> IsEnabledAsync(string key, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
            return entry.Enabled;

        try
        {
            using var conn = connectionFactory.CreateConnection();
            var enabled = await conn.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    "SELECT COALESCE(is_enabled, TRUE) FROM feature_flags WHERE key = @key",
                    new { key },
                    cancellationToken: ct));
            _cache[key] = (enabled, now + CacheTtl);
            return enabled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Feature gate lookup failed for '{Key}' - allowing request", key);
            return true;
        }
    }
}
