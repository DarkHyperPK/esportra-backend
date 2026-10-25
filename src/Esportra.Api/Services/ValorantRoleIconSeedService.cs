using System.Text.Json;
using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Api.Services;

/// <summary>
/// On startup, if any Valorant game roles have icon_url IS NULL, fetches role icons from
/// valorant-api.com and backfills game_catalog_game_roles.icon_url (PROJ-075 T4).
/// The flex role has no Valorant API counterpart and is intentionally skipped.
/// Safe to re-run: UPDATE guarded by icon_url IS NULL.
/// </summary>
public sealed class ValorantRoleIconSeedService(
    IServiceProvider services,
    IHttpClientFactory httpFactory,
    ILogger<ValorantRoleIconSeedService> logger) : IHostedService
{
    private const string ValorantAgentsUrl =
        "https://valorant-api.com/v1/agents?isPlayableCharacter=true";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SeedIfNeededAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Valorant role icon seed failed");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedIfNeededAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var conn = db.CreateConnection();

        var missingCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)::int
            FROM public.game_catalog_game_roles
            WHERE game_slug = 'valorant'
              AND icon_url IS NULL
            """);

        if (missingCount == 0)
        {
            logger.LogDebug("Valorant role icon seed skipped: all rows already have icon_url");
            return;
        }

        logger.LogInformation(
            "Valorant role icon seed: {Count} roles missing icon_url; fetching from valorant-api.com",
            missingCount);

        var client = httpFactory.CreateClient();
        using var response = await client.GetAsync(ValorantAgentsUrl, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Valorant role icon seed: API returned {StatusCode}; icons remain NULL",
                response.StatusCode);
            return;
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            logger.LogWarning("Valorant role icon seed: unexpected API response shape");
            return;
        }

        // Extract unique (displayName, displayIcon) pairs. Each agent has a role object.
        var roleIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in data.EnumerateArray())
        {
            if (!agent.TryGetProperty("role", out var roleEl)) continue;
            if (roleEl.ValueKind == JsonValueKind.Null) continue;

            var displayName = roleEl.GetProperty("displayName").GetString();
            var displayIcon = roleEl.GetProperty("displayIcon").GetString();

            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(displayIcon))
                continue;

            // flex is an Esportra-specific role with no Valorant API counterpart.
            if (displayName.Equals("flex", StringComparison.OrdinalIgnoreCase)) continue;

            roleIcons.TryAdd(displayName, displayIcon);
        }

        if (roleIcons.Count == 0)
        {
            logger.LogWarning("Valorant role icon seed: no usable role icons in API response");
            return;
        }

        var updated = 0;
        foreach (var (displayName, iconUrl) in roleIcons)
        {
            ct.ThrowIfCancellationRequested();
            var rows = await conn.ExecuteAsync(
                """
                UPDATE public.game_catalog_game_roles
                SET icon_url = @iconUrl
                WHERE game_slug = 'valorant'
                  AND lower(display_name) = lower(@displayName)
                  AND icon_url IS NULL
                """,
                new { iconUrl, displayName });
            updated += rows;
        }

        logger.LogInformation(
            "Valorant role icon seed complete: {Updated} rows updated from {Found} unique roles",
            updated, roleIcons.Count);
    }
}
