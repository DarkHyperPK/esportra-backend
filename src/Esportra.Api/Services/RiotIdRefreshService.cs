using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Integrations;
using Npgsql;

namespace Esportra.Api.Services;

/// <summary>Current Riot ID for a PUUID, or why it couldn't be read.</summary>
public sealed record RiotAccountLookupResult(int StatusCode, string? GameName, string? TagLine);

/// <summary>Reads a Riot account by PUUID (account-v1). Separate from the HTTP client so tests can fake Riot.</summary>
public interface IRiotAccountLookup
{
    Task<RiotAccountLookupResult> GetByPuuidAsync(string puuid, CancellationToken ct);
}

public sealed class RiotAccountLookup(RiotApiClient riot, IConfiguration config) : IRiotAccountLookup
{
    // account-v1 serves every account from any regional route.
    private readonly string _route = config["Riot:AccountRoute"] ?? "europe";
    private readonly bool _configured = !string.IsNullOrWhiteSpace(config["Riot:ApiKey"]);

    public async Task<RiotAccountLookupResult> GetByPuuidAsync(string puuid, CancellationToken ct)
    {
        if (!_configured) return new RiotAccountLookupResult(503, null, null);
        var (status, body) = await riot.ProxyAsync(_route, $"/riot/account/v1/accounts/by-puuid/{Uri.EscapeDataString(puuid)}", ct);
        if (status != 200) return new RiotAccountLookupResult(status, null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return new RiotAccountLookupResult(
                200,
                root.TryGetProperty("gameName", out var g) ? g.GetString() : null,
                root.TryGetProperty("tagLine", out var t) ? t.GetString() : null);
        }
        catch (JsonException)
        {
            return new RiotAccountLookupResult(502, null, null);
        }
    }
}

/// <summary>What one refresh pass did.</summary>
public sealed record RiotIdRefreshSummary(int Checked, int Renamed, bool RateLimited);

/// <summary>
/// Keeps linked Riot IDs current. Players can rename their Riot ID at any time; the PUUID never
/// changes, so each linked account is re-read by PUUID. A rename updates <c>riot_accounts</c>,
/// <c>player_riot_accounts</c> and the profile's Riot tag (when it still showed the old ID), and the
/// old name is kept in <c>riot_account_name_history</c> so recent screenshots still verify.
/// </summary>
public sealed class RiotIdRefreshService(IDbConnectionFactory db, IRiotAccountLookup riot, ILogger<RiotIdRefreshService> logger)
{
    private sealed record AccountRow(Guid UserId, string Puuid, string GameName, string TagLine);

    /// <summary>Refresh these users' accounts when their name was last checked before <paramref name="olderThan"/>.</summary>
    public async Task<RiotIdRefreshSummary> RefreshUsersAsync(IReadOnlyCollection<Guid> userIds, TimeSpan olderThan, CancellationToken ct)
    {
        if (userIds.Count == 0) return new RiotIdRefreshSummary(0, 0, false);
        using var conn = db.CreateConnection();
        var accounts = await conn.QueryAsync<AccountRow>(
            """
            SELECT user_id AS UserId, puuid AS Puuid, game_name AS GameName, tag_line AS TagLine
            FROM riot_accounts
            WHERE user_id = ANY(@userIds)
              AND (name_checked_at IS NULL OR name_checked_at < now() - make_interval(secs => @seconds))
            """,
            new { userIds = userIds.ToArray(), seconds = olderThan.TotalSeconds });
        return await RefreshAsync(conn, accounts.ToList(), ct);
    }

    /// <summary>
    /// Members of these competitors (team ids, or solo participant ids as brackets store them), refreshed
    /// when last checked before <paramref name="olderThan"/>.
    /// </summary>
    public async Task<RiotIdRefreshSummary> RefreshCompetitorsAsync(IReadOnlyCollection<Guid> competitorIds, TimeSpan olderThan, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var userIds = await conn.QueryAsync<Guid>(
            """
            SELECT tm.user_id FROM team_members tm WHERE tm.team_id = ANY(@ids) AND tm.is_active = TRUE
            UNION
            SELECT tp.user_id FROM tournament_participants tp WHERE tp.id = ANY(@ids) AND tp.user_id IS NOT NULL
            UNION
            SELECT tm.user_id FROM tournament_participants tp
            JOIN team_members tm ON tm.team_id = tp.team_id AND tm.is_active = TRUE
            WHERE tp.id = ANY(@ids)
            """,
            new { ids = competitorIds.ToArray() });
        return await RefreshUsersAsync(userIds.ToList(), olderThan, ct);
    }

    /// <summary>Everyone on the user's entries in a tournament (their team, or just them when solo).</summary>
    public async Task<RiotIdRefreshSummary> RefreshTournamentEntryAsync(Guid tournamentId, Guid userId, TimeSpan olderThan, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var entries = await conn.QueryAsync<Guid>(
            """
            SELECT id FROM tournament_participants
            WHERE tournament_id = @tournamentId AND (user_id = @userId OR team_captain_id = @userId)
              AND status NOT IN ('cancelled', 'rejected')
            """,
            new { tournamentId, userId });
        return await RefreshCompetitorsAsync(entries.Append(userId).ToList(), olderThan, ct);
    }

    /// <summary>
    /// Scheduled safety net. Event refreshes (registration, check-in, team join, screenshot upload)
    /// keep active players current; this sweep picks up players in tournaments that are still running
    /// whose name wasn't checked for a day, and everyone else once a week. Oldest check first.
    /// </summary>
    public async Task<RiotIdRefreshSummary> RefreshStaleAsync(int batchSize, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var accounts = await conn.QueryAsync<AccountRow>(
            """
            WITH active AS (
              SELECT ra.user_id, ra.puuid, ra.game_name, ra.tag_line, ra.name_checked_at,
                     EXISTS (
                       SELECT 1 FROM tournament_participants tp
                       JOIN tournaments t ON t.id = tp.tournament_id
                       LEFT JOIN team_members tm ON tm.team_id = tp.team_id AND tm.is_active = TRUE
                       WHERE (tp.user_id = ra.user_id OR tm.user_id = ra.user_id)
                         AND t.status NOT IN ('completed', 'cancelled')
                         AND tp.status NOT IN ('cancelled', 'rejected')
                     ) AS playing
              FROM riot_accounts ra
            )
            SELECT user_id AS UserId, puuid AS Puuid, game_name AS GameName, tag_line AS TagLine
            FROM active
            WHERE name_checked_at IS NULL
               OR (playing AND name_checked_at < now() - interval '24 hours')
               OR name_checked_at < now() - interval '7 days'
            ORDER BY playing DESC, name_checked_at NULLS FIRST
            LIMIT @batchSize
            """,
            new { batchSize });
        return await RefreshAsync(conn, accounts.ToList(), ct);
    }

    /// <summary>
    /// Riot sign-in returned the account's current name: keep the previous one in history when it
    /// changed and update the desktop-agent link, as a scheduled refresh would. Call before the
    /// sign-in overwrites <c>riot_accounts</c>.
    /// </summary>
    public static Task RecordSignInNameAsync(IDbConnection conn, Guid userId, string puuid, string gameName, string tagLine) =>
        conn.ExecuteAsync(
            """
            INSERT INTO riot_account_name_history (puuid, game_name, tag_line)
            SELECT puuid, game_name, tag_line FROM riot_accounts
            WHERE user_id = @userId AND puuid = @puuid AND (game_name <> @gameName OR tag_line <> @tagLine);
            UPDATE player_riot_accounts SET game_name = @gameName, tag_line = @tagLine
            WHERE riot_puuid = @puuid AND (game_name IS DISTINCT FROM @gameName OR tag_line IS DISTINCT FROM @tagLine);
            """,
            new { userId, puuid, gameName, tagLine });

    private async Task<RiotIdRefreshSummary> RefreshAsync(IDbConnection conn, List<AccountRow> accounts, CancellationToken ct)
    {
        int checkedCount = 0, renamed = 0;
        foreach (var account in accounts)
        {
            var current = await riot.GetByPuuidAsync(account.Puuid, ct);
            if (current.StatusCode is 429 or >= 500)
            {
                logger.LogInformation("Riot ID refresh paused after {Checked} accounts (status {Status})", checkedCount, current.StatusCode);
                return new RiotIdRefreshSummary(checkedCount, renamed, current.StatusCode == 429);
            }
            checkedCount++;
            var changed = current.StatusCode == 200
                && !string.IsNullOrWhiteSpace(current.GameName) && !string.IsNullOrWhiteSpace(current.TagLine)
                && (current.GameName != account.GameName || current.TagLine != account.TagLine);
            if (changed)
            {
                await ApplyRenameAsync(conn, account, current.GameName!, current.TagLine!);
                renamed++;
            }
            else
            {
                await conn.ExecuteAsync("UPDATE riot_accounts SET name_checked_at = now() WHERE puuid = @Puuid", new { account.Puuid });
            }
        }
        return new RiotIdRefreshSummary(checkedCount, renamed, false);
    }

    private async Task ApplyRenameAsync(IDbConnection conn, AccountRow account, string gameName, string tagLine)
    {
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var param = new { account.UserId, account.Puuid, OldName = account.GameName, OldTag = account.TagLine, gameName, tagLine };
        await conn.ExecuteAsync(
            """
            INSERT INTO riot_account_name_history (puuid, game_name, tag_line) VALUES (@Puuid, @OldName, @OldTag);
            UPDATE riot_accounts SET game_name = @gameName, tag_line = @tagLine, name_checked_at = now(), updated_at = now()
            WHERE puuid = @Puuid;
            UPDATE player_riot_accounts SET game_name = @gameName, tag_line = @tagLine WHERE riot_puuid = @Puuid;
            """,
            param, tx);
        tx.Commit();
        await SyncProfileTagAsync(conn, param.UserId, $"{account.GameName}#{account.TagLine}", $"{gameName}#{tagLine}");
        logger.LogInformation("Riot ID of user {UserId} changed; linked account updated", account.UserId);
    }

    /// <summary>The profile tag follows the rename only when it still showed the old linked ID.</summary>
    private async Task SyncProfileTagAsync(IDbConnection conn, Guid userId, string oldTag, string newTag)
    {
        try
        {
            await conn.ExecuteAsync(
                "UPDATE profiles SET riot_tag = @newTag WHERE id = @userId AND riot_tag = @oldTag",
                new { userId, oldTag, newTag });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            logger.LogWarning("Riot tag {Tag} is already on another profile; left user {UserId}'s profile tag unchanged", newTag, userId);
        }
    }
}
