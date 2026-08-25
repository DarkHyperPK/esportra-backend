using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Core.Tournaments;

namespace Esportra.Api.Services;

/// <summary>
/// Writes org-scoped audit entries when owners or assigned staff act on tournaments.
/// Captain/player actions are not logged here — callers must only invoke after staff auth passes.
/// </summary>
public sealed class StaffTournamentAuditService(ILogger<StaffTournamentAuditService> logger, IHttpContextAccessor httpContextAccessor)
{
    public Task TryLogMatchActionAsync(
        IDbConnection conn,
        Guid actorUserId,
        Guid matchId,
        string action,
        object? details = null,
        IDbTransaction? tx = null,
        CancellationToken ct = default) =>
        TryLogInternalAsync(conn, actorUserId, action, "match", matchId, async () =>
        {
            var row = await conn.QuerySingleOrDefaultAsync<MatchAuditRow>(
                $"""
                SELECT o.id AS OrganizationId,
                       t.id AS TournamentId,
                       t.name AS TournamentName,
                       t.slug AS TournamentSlug,
                       m.id AS MatchId,
                       m.match_number AS MatchNumber,
                       m.round_index AS RoundIndex,
                       {BracketTeamResolutionSql.Team1Columns},
                       {BracketTeamResolutionSql.Team2Columns},
                       CASE
                         WHEN o.owner_id = @actorUserId THEN 'owner'
                         WHEN os.role IS NOT NULL THEN os.role
                         ELSE NULL
                       END AS ActorRole
                FROM brkt_matches m
                JOIN brkt_versions v ON v.id = m.version_id
                JOIN tournaments t ON t.id = v.tournament_id
                JOIN organizations o ON o.id = t.organization_id
                {BracketTeamResolutionSql.Team1Joins}
                {BracketTeamResolutionSql.Team2Joins}
                LEFT JOIN organization_staff os
                  ON os.organization_id = o.id
                 AND os.user_id = @actorUserId
                 AND os.status = 'active'
                LEFT JOIN staff_tournament_assignments sta
                  ON sta.organization_staff_id = os.id
                 AND sta.tournament_id = t.id
                WHERE m.id = @matchId
                  AND (
                    o.owner_id = @actorUserId
                    OR (
                      os.id IS NOT NULL
                      AND (os.role = 'admin' OR sta.id IS NOT NULL)
                    )
                  )
                """,
                new { matchId, actorUserId }, tx);

            if (row is null || string.IsNullOrEmpty(row.ActorRole))
                return null;

            var matchLabel = FormatMatchLabel(row.MatchNumber, row.RoundIndex);
            var baseDetails = new Dictionary<string, object?>
            {
                ["tournament_id"] = row.TournamentId.ToString(),
                ["tournament_name"] = row.TournamentName,
                ["tournament_slug"] = row.TournamentSlug,
                ["match_id"] = row.MatchId.ToString(),
                ["match_number"] = row.MatchNumber,
                ["round_index"] = row.RoundIndex,
                ["match_label"] = matchLabel,
                ["matchup"] = FormatMatchup(row.Team1Name, row.Team2Name),
                ["actor_role"] = row.ActorRole,
            };

            return (row.OrganizationId, MergeDetails(baseDetails, details));
        }, tx, ct);

    public Task TryLogTournamentActionAsync(
        IDbConnection conn,
        Guid actorUserId,
        Guid tournamentId,
        string action,
        string targetType,
        Guid? targetId,
        object? details = null,
        IDbTransaction? tx = null,
        CancellationToken ct = default) =>
        TryLogInternalAsync(conn, actorUserId, action, targetType, targetId, async () =>
        {
            var row = await conn.QuerySingleOrDefaultAsync<TournamentAuditRow>(
                """
                SELECT o.id AS OrganizationId,
                       t.id AS TournamentId,
                       t.name AS TournamentName,
                       t.slug AS TournamentSlug,
                       CASE
                         WHEN o.owner_id = @actorUserId THEN 'owner'
                         WHEN os.role IS NOT NULL THEN os.role
                         ELSE NULL
                       END AS ActorRole
                FROM tournaments t
                JOIN organizations o ON o.id = t.organization_id
                LEFT JOIN organization_staff os
                  ON os.organization_id = o.id
                 AND os.user_id = @actorUserId
                 AND os.status = 'active'
                LEFT JOIN staff_tournament_assignments sta
                  ON sta.organization_staff_id = os.id
                 AND sta.tournament_id = t.id
                WHERE t.id = @tournamentId
                  AND (
                    o.owner_id = @actorUserId
                    OR (
                      os.id IS NOT NULL
                      AND (os.role = 'admin' OR sta.id IS NOT NULL)
                    )
                  )
                """,
                new { tournamentId, actorUserId }, tx);

            if (row is null || string.IsNullOrEmpty(row.ActorRole))
                return null;

            var baseDetails = new Dictionary<string, object?>
            {
                ["tournament_id"] = row.TournamentId.ToString(),
                ["tournament_name"] = row.TournamentName,
                ["tournament_slug"] = row.TournamentSlug,
                ["actor_role"] = row.ActorRole,
            };

            return (row.OrganizationId, MergeDetails(baseDetails, details));
        }, tx, ct);

    public async Task TryLogStageActionAsync(
        IDbConnection conn,
        Guid actorUserId,
        Guid stageId,
        string action,
        object? details = null,
        IDbTransaction? tx = null,
        CancellationToken ct = default)
    {
        var tournamentId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT tournament_id FROM tournament_stages WHERE id = @stageId",
            new { stageId }, tx);
        if (tournamentId is null)
            return;

        await TryLogTournamentActionAsync(
            conn, actorUserId, tournamentId.Value, action, "stage", stageId, details, tx, ct);
    }

    private async Task TryLogInternalAsync(
        IDbConnection conn,
        Guid actorUserId,
        string action,
        string targetType,
        Guid? targetId,
        Func<Task<(Guid OrgId, Dictionary<string, object?> Details)?>> resolve,
        IDbTransaction? tx,
        CancellationToken ct)
    {
        try
        {
            var resolved = await resolve();
            if (resolved is null)
                return;

            await conn.ExecuteAsync(
                """
                INSERT INTO staff_audit_log (organization_id, actor_id, action, target_type, target_id, details, ip_address, user_agent)
                VALUES (@orgId, @actorId, @action, @targetType, @targetId, @details::jsonb, @ip::inet, @userAgent)
                """,
                new
                {
                    orgId = resolved.Value.OrgId,
                    actorId = actorUserId,
                    ip = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString(),
                    userAgent = httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString(),
                    action,
                    targetType,
                    targetId,
                    details = JsonSerializer.Serialize(resolved.Value.Details),
                },
                tx);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[StaffTournamentAudit] Failed to log {Action} for actor {ActorId}", action, actorUserId);
        }
    }

    private static Dictionary<string, object?> MergeDetails(
        Dictionary<string, object?> baseDetails,
        object? extra)
    {
        if (extra is null)
            return baseDetails;

        if (extra is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
                baseDetails[pair.Key] = pair.Value;
            return baseDetails;
        }

        var json = JsonSerializer.Serialize(extra);
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
            baseDetails[prop.Name] = JsonElementToObject(prop.Value);

        return baseDetails;
    }

    private static object? JsonElementToObject(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };

    internal static string FormatMatchLabel(int matchNumber, int roundIndex) =>
        roundIndex >= 0
            ? $"Round {roundIndex + 1}, Match {matchNumber}"
            : $"Match {matchNumber}";

    internal static Dictionary<string, object?> MergeDetailsForTest(
        Dictionary<string, object?> baseDetails,
        object? extra) => MergeDetails(baseDetails, extra);

    private static string FormatMatchup(string? team1Name, string? team2Name)
    {
        var left = string.IsNullOrWhiteSpace(team1Name) ? "TBD" : team1Name.Trim();
        var right = string.IsNullOrWhiteSpace(team2Name) ? "TBD" : team2Name.Trim();
        return $"{left} vs {right}";
    }

    private sealed class MatchAuditRow
    {
        public Guid OrganizationId { get; init; }
        public Guid TournamentId { get; init; }
        public string? TournamentName { get; init; }
        public string? TournamentSlug { get; init; }
        public Guid MatchId { get; init; }
        public int MatchNumber { get; init; }
        public int RoundIndex { get; init; }
        public string? Team1Name { get; init; }
        public string? Team2Name { get; init; }
        public string? ActorRole { get; init; }
    }

    private sealed class TournamentAuditRow
    {
        public Guid OrganizationId { get; init; }
        public Guid TournamentId { get; init; }
        public string? TournamentName { get; init; }
        public string? TournamentSlug { get; init; }
        public string? ActorRole { get; init; }
    }
}
