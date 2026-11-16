using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Email;

namespace Esportra.Api.Services;

/// <summary>
/// Best-effort email delivery for LFG and Scrim lifecycle events.
/// Checks the per-user email opt-out in profiles.settings before sending.
/// Never throws — all failures are logged and swallowed.
/// </summary>
public sealed class CompetitiveEmailService(
    IDbConnectionFactory db,
    IEmailService emailService,
    IConfiguration config,
    ILogger<CompetitiveEmailService> logger)
{
    public string FrontendUrl => (config["FrontendUrl"] ?? "https://esportra.com").TrimEnd('/');

    public Task SendLfgAsync(Guid userId, EmailType type, object data, CancellationToken ct = default)
        => SendAsync(userId, "email_lfg", type, data, ct);

    public Task SendScrimAsync(Guid userId, EmailType type, object data, CancellationToken ct = default)
        => SendAsync(userId, "email_scrims", type, data, ct);

    public async Task SendScrimToTeamAsync(
        Guid teamId, IDbConnection conn, EmailType type, object data, CancellationToken ct = default)
    {
        var staffIds = await GetTeamStaffIdsAsync(conn, teamId);
        foreach (var userId in staffIds)
            await SendScrimAsync(userId, type, data, ct);
    }

    private async Task SendAsync(Guid userId, string prefKey, EmailType type, object data, CancellationToken ct)
    {
        try
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<ProfileRow>(
                "SELECT email, settings::text AS settings FROM public.profiles WHERE id = @userId",
                new { userId });

            if (string.IsNullOrWhiteSpace(row?.Email))
                return;

            if (!IsEmailEnabled(row.Settings, prefKey))
                return;

            await emailService.SendAsync(row.Email, type, data, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[CompetitiveEmail] Failed to send {Type} to user {UserId}", type, userId);
        }
    }

    private static bool IsEmailEnabled(string? settingsJson, string prefKey)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return true; // default on when key absent

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.TryGetProperty(prefKey, out var pref) &&
                pref.ValueKind == JsonValueKind.False)
                return false;
        }
        catch
        {
            // malformed JSON — treat as default on
        }

        return true;
    }

    internal static async Task<IReadOnlyList<Guid>> GetTeamStaffIdsAsync(IDbConnection conn, Guid teamId)
    {
        return (await conn.QueryAsync<Guid>(
            """
            SELECT user_id FROM team_members
            WHERE team_id = @teamId
              AND role IN ('captain', 'manager')
              AND is_active = TRUE
              AND user_id IS NOT NULL
            """,
            new { teamId })).AsList();
    }

    private sealed record ProfileRow(string? Email, string? Settings);
}
