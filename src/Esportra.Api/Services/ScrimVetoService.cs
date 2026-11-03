using System.Security.Cryptography;
using Dapper;
using Esportra.Api.ScheduledJobs;
using Esportra.Contracts.Database;
using Esportra.Core.Match;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Esportra.Api.Services;

/// <summary>
/// Creates a public_veto_sessions row for a scrim session when both teams check in
/// (for veto-enabled games: valorant, cs2 by default — config-driven via Scrims:VetoEnabledGames).
///
/// Security: raw tokens are stored in scrim_sessions columns that are never returned
/// from API responses. GetSessionAsync derives veto_my_token per caller's team.
///
/// Idempotent: if veto_session_id is already set on the session, returns it unchanged.
/// </summary>
public sealed class ScrimVetoService(
    IDbConnectionFactory db,
    IOptions<ScrimVetoOptions> options,
    IBackgroundJobClient backgroundJobs,
    ILogger<ScrimVetoService> logger)
{
    private static string GenerateToken() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>
    /// Creates a veto session for the scrim if the game requires it.
    /// Returns (true, vetoSessionId) on success or if already created.
    /// Returns (false, null) if the game does not require veto.
    /// On creation failure: returns (false, null), logs the error, session proceeds to in_progress.
    /// </summary>
    public async Task<(bool Created, Guid? VetoSessionId)> TryCreateVetoSessionAsync(
        Guid sessionId,
        string? gameSlug,
        string team1Name,
        string team2Name,
        CancellationToken ct = default)
    {
        var vetoEnabledGames = options.Value.VetoEnabledGames;
        if (string.IsNullOrWhiteSpace(gameSlug) ||
            !vetoEnabledGames.Contains(gameSlug, StringComparer.OrdinalIgnoreCase))
        {
            return (false, null);
        }

        using var conn = db.CreateConnection();

        // Idempotency: check if already created.
        var existing = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT veto_session_id FROM public.scrim_sessions WHERE id = @sessionId",
            new { sessionId });
        // The veto follows the format the listing promised (Bo1/Bo3/Bo5).
        var sessionBestOf = await conn.ExecuteScalarAsync<int?>(
            "SELECT best_of FROM public.scrim_sessions WHERE id = @sessionId",
            new { sessionId });

        if (existing is not null)
        {
            logger.LogInformation("[ScrimVetoService] Veto session already exists for scrim {SessionId}: {VetoId}.", sessionId, existing);
            return (true, existing);
        }

        try
        {
            var gameConfig = VetoSequences.GetGameConfig(gameSlug);

            // Fetch the active map pool for the game.
            var mapPool = (await conn.QueryAsync<string>(
                """
                SELECT id::text
                FROM public.game_maps
                WHERE game ILIKE @gameSlug
                  AND is_active = TRUE
                ORDER BY map_name
                LIMIT @poolSize
                """,
                new { gameSlug, poolSize = gameConfig.MapPoolSize })).ToArray();

            var vetoId = Guid.NewGuid();
            var team1Id = Guid.NewGuid();
            var team2Id = Guid.NewGuid();
            var hostToken = GenerateToken();
            var team1Token = GenerateToken();
            var team2Token = GenerateToken();
            var overlayToken = GenerateToken();

            var bestOf = sessionBestOf is 1 or 3 or 5 ? sessionBestOf.Value : 1;

            await conn.ExecuteAsync(
                """
                INSERT INTO public.public_veto_sessions
                    (id, game, best_of, team1_name, team2_name, team1_id, team2_id,
                     current_action_number, status,
                     toss_winner_team_id, toss_completed_at,
                     selected_map_pool, host_token, team1_token, team2_token, overlay_token)
                VALUES
                    (@id, @game, @bestOf, @team1Name, @team2Name, @team1Id, @team2Id,
                     1, 'toss_choice_pending',
                     @tossWinnerId, now(),
                     @mapPool, @hostToken, @team1Token, @team2Token, @overlayToken)
                """,
                new
                {
                    id = vetoId,
                    game = gameSlug,
                    bestOf,
                    team1Name,
                    team2Name,
                    team1Id,
                    team2Id,
                    // Scrims have no host to flip the coin, so the toss is resolved here.
                    tossWinnerId = (RandomNumberGenerator.GetBytes(1)[0] & 1) == 0 ? team1Id : team2Id,
                    mapPool,
                    hostToken,
                    team1Token,
                    team2Token,
                    overlayToken,
                });

            // Store veto session reference + raw tokens on the session row.
            // Tokens are never returned via API — derived per caller in GetSessionAsync.
            await conn.ExecuteAsync(
                """
                UPDATE public.scrim_sessions
                SET veto_session_id   = @vetoId,
                    veto_team1_token  = @team1Token,
                    veto_team2_token  = @team2Token,
                    updated_at        = now()
                WHERE id = @sessionId
                """,
                new { sessionId, vetoId, team1Token, team2Token });

            // First abandon check after one inactivity window; the job re-arms itself while the veto is still being acted on.
            var abandonJobId = backgroundJobs.Schedule<ScrimVetoAbandonJob>(
                j => j.RunAsync(sessionId, vetoId, CancellationToken.None),
                TimeSpan.FromMinutes(Math.Max(1, options.Value.VetoInactivityMinutes)));

            // Merge the abandon job ID into hangfire_job_ids so CancelSessionAsync deletes it.
            var vetoJobJson = System.Text.Json.JsonSerializer.Serialize(new { vetoAbandon = abandonJobId });
            await conn.ExecuteAsync(
                """
                UPDATE public.scrim_sessions
                SET hangfire_job_ids = COALESCE(hangfire_job_ids, '{}'::jsonb) || @vetoJobJson::jsonb
                WHERE id = @sessionId
                """,
                new { sessionId, vetoJobJson });

            logger.LogInformation("[ScrimVetoService] Created veto session {VetoId} for scrim {SessionId} (game={Game}).",
                vetoId, sessionId, gameSlug);

            return (true, vetoId);
        }
        catch (Exception ex)
        {
            // Best-effort: veto creation failure must not block the session from going in_progress.
            logger.LogError(ex, "[ScrimVetoService] Failed to create veto session for scrim {SessionId} (game={Game}). Session will proceed without veto.",
                sessionId, gameSlug);
            return (false, null);
        }
    }
}
