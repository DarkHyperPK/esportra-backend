using System.Text.Json;
using Dapper;
using Esportra.Contracts.Database;
using Microsoft.Extensions.Logging;

namespace Esportra.Infrastructure.Integrations;

/// <summary>
/// Compares companion-reported Valorant stats against the Riot Match API after a match ends.
/// Outcomes: VERIFIED (K/D/A match), API_SOURCED (no companion row, insert from API),
/// or unverifiable (API unavailable — leave as companion, verified = false).
/// </summary>
public sealed class RiotMatchValidationService(
    IDbConnectionFactory db,
    RiotApiClient riotApi,
    ILogger<RiotMatchValidationService> logger)
{
    /// <summary>
    /// Validates or sources stats for <paramref name="matchId"/> + <paramref name="puuid"/>.
    /// Throws <see cref="RiotRateLimitException"/> on Riot API 429 so Hangfire retries.
    /// Never throws for any other failure — logs and returns.
    /// </summary>
    public async Task ValidateAsync(
        Guid matchId, string riotMatchId, string puuid, string? region, CancellationToken ct)
    {
        try
        {
            var (statusCode, body) = await riotApi.GetMatchAsync(riotMatchId, region, ct);

            if (statusCode == 429)
                throw new RiotRateLimitException($"Riot API rate-limited for match {riotMatchId}");

            if (statusCode == 404)
            {
                logger.LogInformation(
                    "Riot API: match {RiotMatchId} not found (404). Leaving companion row unverified.",
                    riotMatchId);
                return;
            }

            if (statusCode != 200)
            {
                logger.LogWarning(
                    "Riot API: unexpected status {Status} for match {RiotMatchId}. Leaving unverified.",
                    statusCode, riotMatchId);
                return;
            }

            var apiPlayer = ExtractPlayerStats(body, puuid);
            if (apiPlayer is null)
            {
                logger.LogWarning(
                    "Riot API: PUUID {Puuid} not found in match {RiotMatchId}.", puuid, riotMatchId);
                return;
            }

            using var conn = db.CreateConnection();
            var existing = await conn.QuerySingleOrDefaultAsync<CompanionStatRow>(
                """
                SELECT id, kills, deaths, assists
                FROM match_player_stats
                WHERE match_id = @matchId AND riot_match_id = @riotMatchId
                  AND stat_source = 'companion'
                """,
                new { matchId, riotMatchId });

            if (existing is not null)
                await ReconcileCompanionRow(conn, existing, apiPlayer, riotMatchId);
            else
                await InsertRiotSourcedRow(conn, matchId, riotMatchId, puuid, apiPlayer);
        }
        catch (RiotRateLimitException)
        {
            throw; // Let Hangfire retry
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "RiotMatchValidationService: unexpected error for match {MatchId} / riot {RiotMatchId}",
                matchId, riotMatchId);
        }
    }

    private async Task ReconcileCompanionRow(
        System.Data.IDbConnection conn,
        CompanionStatRow existing,
        RiotPlayerStats api,
        string riotMatchId)
    {
        bool kdaMatches = existing.Kills == api.Kills
                       && existing.Deaths == api.Deaths
                       && existing.Assists == api.Assists;

        if (kdaMatches)
        {
            await conn.ExecuteAsync(
                "UPDATE match_player_stats SET verified = true WHERE id = @id",
                new { existing.Id });
            logger.LogInformation(
                "Riot validation VERIFIED: stat row {Id}, riot match {RiotMatchId}",
                existing.Id, riotMatchId);
        }
        else
        {
            logger.LogWarning(
                "Riot validation CONFLICT: stat row {Id}, riot match {RiotMatchId}. " +
                "Companion K/D/A={CK}/{CD}/{CA}; Riot K/D/A={RK}/{RD}/{RA}",
                existing.Id, riotMatchId,
                existing.Kills, existing.Deaths, existing.Assists,
                api.Kills, api.Deaths, api.Assists);
            // Leave as unverified — stat remains with companion values; ops can review
        }
    }

    private async Task InsertRiotSourcedRow(
        System.Data.IDbConnection conn,
        Guid matchId, string riotMatchId, string puuid, RiotPlayerStats api)
    {
        await conn.ExecuteAsync(
            """
            INSERT INTO match_player_stats
                (match_id, map_number, stat_source, riot_match_id, verified,
                 kills, deaths, assists, extra_data, created_at)
            VALUES
                (@matchId, 1, 'riot_api', @riotMatchId, false,
                 @kills, @deaths, @assists,
                 jsonb_build_object('puuid', @puuid, 'source', 'riot_api'),
                 NOW())
            ON CONFLICT (match_id, riot_match_id, user_id)
            WHERE riot_match_id IS NOT NULL AND user_id IS NOT NULL
            DO NOTHING
            """,
            new { matchId, riotMatchId, kills = api.Kills, deaths = api.Deaths, assists = api.Assists, puuid });

        logger.LogInformation(
            "Riot validation API_SOURCED: inserted riot_api row for match {MatchId}, riot {RiotMatchId}",
            matchId, riotMatchId);
    }

    private static RiotPlayerStats? ExtractPlayerStats(string body, string puuid)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("players", out var players)) return null;

            foreach (var player in players.EnumerateArray())
            {
                if (!player.TryGetProperty("puuid", out var puuidEl)) continue;
                if (!string.Equals(puuidEl.GetString(), puuid, StringComparison.Ordinal)) continue;
                if (!player.TryGetProperty("stats", out var stats)) return null;

                return new RiotPlayerStats(
                    Kills: stats.TryGetProperty("kills", out var k) ? k.GetInt32() : 0,
                    Deaths: stats.TryGetProperty("deaths", out var d) ? d.GetInt32() : 0,
                    Assists: stats.TryGetProperty("assists", out var a) ? a.GetInt32() : 0);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record CompanionStatRow(Guid Id, int Kills, int Deaths, int Assists);
    private sealed record RiotPlayerStats(int Kills, int Deaths, int Assists);
}

/// <summary>Thrown when the Riot API returns HTTP 429. Signals Hangfire to retry with backoff.</summary>
public sealed class RiotRateLimitException(string message) : Exception(message);
