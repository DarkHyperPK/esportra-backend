using Esportra.Infrastructure.Integrations;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Esportra.Api.Jobs;

/// <summary>
/// Hangfire job that cross-validates a companion-reported Valorant stat against the Riot Match API.
/// Enqueued with a 5-minute delay (match indexing delay) via BackgroundJob.Schedule.
/// Retries up to 3 times with exponential backoff on Riot API rate limit (429).
/// Never throws for any other failure — logs and moves on.
/// Config: Riot:ApiKey from environment (never appsettings.json).
/// </summary>
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 180, 300], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class RiotMatchValidationJob(
    RiotMatchValidationService validationService,
    ILogger<RiotMatchValidationJob> logger)
{
    /// <summary>
    /// Execute validation for one companion stat row.
    /// Throws <see cref="RiotRateLimitException"/> on 429 to trigger Hangfire retry.
    /// </summary>
    public async Task ExecuteAsync(
        Guid matchId, string riotMatchId, string puuid, string? region, CancellationToken ct)
    {
        logger.LogInformation(
            "RiotMatchValidationJob: validating match {MatchId}, riot {RiotMatchId}, puuid {Puuid}",
            matchId, riotMatchId, puuid);

        await validationService.ValidateAsync(matchId, riotMatchId, puuid, region, ct);
    }
}
