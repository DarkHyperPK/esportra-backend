using Dapper;
using Esportra.Contracts;
using Microsoft.Extensions.Logging;

namespace Esportra.Infrastructure.Integrations;

/// <summary>
/// Refreshes gameName and tagLine for a linked Riot account by querying the
/// /riot/account/v1/accounts/by-puuid endpoint using the server API key.
/// The PUUID is the stable identifier; the name is display-only and can change.
/// </summary>
public sealed class RiotAccountRefreshService(
    RiotApiClient riotApi,
    IDbConnectionFactory db,
    ILogger<RiotAccountRefreshService> logger)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private sealed record RiotAccountRow(
        string Puuid,
        string GameName,
        string TagLine,
        string? Region,
        DateTime? LastRefreshedAt);

    /// <summary>
    /// Fetches the current Riot name for the user's linked account if the cached data
    /// is older than 24 hours (or has never been refreshed). Updates riot_accounts and
    /// profiles.riot_tag when the name has changed.
    /// Returns true if a refresh was performed, false if skipped or failed.
    /// </summary>
    public async Task<bool> RefreshIfStaleAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            using var conn = db.CreateConnection();

            var row = await conn.QuerySingleOrDefaultAsync<RiotAccountRow>(
                """
                SELECT puuid, game_name AS GameName, tag_line AS TagLine,
                       region, last_refreshed_at AS LastRefreshedAt
                FROM public.riot_accounts
                WHERE user_id = @userId
                """,
                new { userId });

            if (row is null) return false;

            if (row.LastRefreshedAt.HasValue &&
                DateTime.UtcNow - row.LastRefreshedAt.Value < MaxAge)
                return false;

            var result = await riotApi.GetAccountByPuuidAsync(row.Puuid, row.Region, ct);
            if (result is null)
            {
                logger.LogWarning(
                    "Riot name refresh failed for user {UserId} (PUUID {Puuid}) — API returned no data",
                    userId, row.Puuid);
                return false;
            }

            var (gameName, tagLine) = result.Value;

            await conn.ExecuteAsync(
                """
                UPDATE public.riot_accounts
                SET game_name = @gameName, tag_line = @tagLine, last_refreshed_at = now()
                WHERE user_id = @userId
                """,
                new { userId, gameName, tagLine });

            var newRiotTag = $"{gameName}#{tagLine}";
            var oldRiotTag = $"{row.GameName}#{row.TagLine}";
            if (!string.Equals(newRiotTag, oldRiotTag, StringComparison.OrdinalIgnoreCase))
            {
                await conn.ExecuteAsync(
                    "UPDATE public.profiles SET riot_tag = @riotTag WHERE id = @userId",
                    new { riotTag = newRiotTag, userId });

                logger.LogInformation(
                    "Riot name updated for user {UserId}: {OldTag} → {NewTag}",
                    userId, oldRiotTag, newRiotTag);
            }
            else
            {
                // Name unchanged — still update last_refreshed_at (already done above)
                logger.LogDebug(
                    "Riot name unchanged for user {UserId} ({Tag})", userId, newRiotTag);
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Riot name refresh failed for user {UserId}", userId);
            return false;
        }
    }
}
