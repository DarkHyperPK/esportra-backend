using System.Data;
using Dapper;
using Esportra.Api.Services;
using Esportra.Core.Br;

namespace Esportra.Api.Helpers;

public sealed record StageCompletionSnapshot(
    bool IsComplete,
    bool AlreadyAdvanced,
    string ProgressLabel,
    string? Reason,
    int GroupsTotal = 0,
    int GroupsWithCompletedRounds = 0,
    IReadOnlyList<object> AdvancingTeams = null!)
{
    public IReadOnlyList<object> AdvancingTeams { get; init; } = AdvancingTeams ?? Array.Empty<object>();

    public object ToResponse() => new
    {
        isComplete = IsComplete,
        alreadyAdvanced = AlreadyAdvanced,
        progressLabel = ProgressLabel,
        reason = Reason,
        groupsTotal = GroupsTotal,
        groupsWithCompletedRounds = GroupsWithCompletedRounds,
        advancingTeams = AdvancingTeams,
    };
}

public static class StageCompletionHelper
{
    public static async Task<StageCompletionSnapshot> EvaluateAsync(
        IDbConnection conn,
        dynamic stage,
        Guid stageId,
        CancellationToken ct = default)
    {
        var format = ((string?)stage.format ?? "single_elimination").ToLowerInvariant();
        var alreadyAdvanced = await IsStageAlreadyAdvancedAsync(conn, stage, stageId);

        if (format is "battle_royale")
            return await EvaluateBattleRoyaleAsync(conn, stage, stageId, alreadyAdvanced, ct);

        return new StageCompletionSnapshot(
            IsComplete: false,
            AlreadyAdvanced: alreadyAdvanced,
            ProgressLabel: alreadyAdvanced ? "advanced" : "setup",
            Reason: "Bracket completion is evaluated separately.");
    }

    public static async Task<StageCompletionSnapshot> EvaluateBattleRoyaleAsync(
        IDbConnection conn,
        dynamic stage,
        Guid stageId,
        bool? alreadyAdvancedOverride = null,
        CancellationToken ct = default)
    {
        var alreadyAdvanced = alreadyAdvancedOverride ?? await IsStageAlreadyAdvancedAsync(conn, stage, stageId);
        if (alreadyAdvanced)
        {
            return new StageCompletionSnapshot(
                IsComplete: true,
                AlreadyAdvanced: true,
                ProgressLabel: "advanced",
                Reason: "Teams have already been advanced to the next stage.");
        }

        var stageMeta = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT ts.config::text AS stage_config, t.settings
            FROM tournament_stages ts
            JOIN tournaments t ON t.id = ts.tournament_id
            WHERE ts.id = @stageId
            """,
            new { stageId });
        var stageConfig = stageMeta?.stage_config as string;
        var format = BrConfigService.ResolveFormat(stageConfig);
        var gamesPerLobby = BrConfigService.ResolveGamesPerLobby(
            stageMeta?.settings, stageConfig) ?? 6;

        var groups = (await conn.QueryAsync<Guid>(
            "SELECT id FROM br_groups WHERE stage_id = @stageId ORDER BY group_order",
            new { stageId })).ToList();

        if (groups.Count == 0)
        {
            return new StageCompletionSnapshot(
                IsComplete: false,
                AlreadyAdvanced: false,
                ProgressLabel: "setup",
                Reason: "No groups configured yet.",
                GroupsTotal: 0,
                GroupsWithCompletedRounds: 0);
        }

        var totalLobbies = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*)::int FROM br_lobbies WHERE stage_id = @stageId",
            new { stageId });

        var totalGames = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)::int
            FROM br_games g
            JOIN br_lobbies l ON l.id = g.lobby_id
            WHERE l.stage_id = @stageId
            """,
            new { stageId });

        var completedGames = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)::int
            FROM br_games g
            JOIN br_lobbies l ON l.id = g.lobby_id
            WHERE l.stage_id = @stageId
              AND g.status = 'completed'
            """,
            new { stageId });

        var expectedGames = totalLobbies * gamesPerLobby;
        var hasActiveGame = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM br_games g
                JOIN br_lobbies l ON l.id = g.lobby_id
                WHERE l.stage_id = @stageId
                  AND g.status = 'active'
            )
            """,
            new { stageId });

        var usesWaveCompletion = format is BrStageFormat.GroupRotation
            or BrStageFormat.MultiLobbyCut;

        if (usesWaveCompletion)
        {
            if (totalLobbies == 0)
            {
                return new StageCompletionSnapshot(
                    IsComplete: false,
                    AlreadyAdvanced: false,
                    ProgressLabel: "setup",
                    Reason: "Schedule not materialized yet — generate the lobby schedule first.",
                    GroupsTotal: groups.Count,
                    GroupsWithCompletedRounds: 0);
            }

            if (totalGames < expectedGames)
            {
                return new StageCompletionSnapshot(
                    IsComplete: false,
                    AlreadyAdvanced: false,
                    ProgressLabel: "setup",
                    Reason: $"Games not fully scheduled ({totalGames}/{expectedGames} games materialized).",
                    GroupsTotal: groups.Count,
                    GroupsWithCompletedRounds: 0);
            }

            var pendingGames = expectedGames - completedGames;
            if (pendingGames > 0)
            {
                var progressLabel = hasActiveGame || completedGames > 0 ? "in_progress" : "setup";
                return new StageCompletionSnapshot(
                    IsComplete: false,
                    AlreadyAdvanced: false,
                    ProgressLabel: progressLabel,
                    Reason: $"{pendingGames} of {expectedGames} scheduled games are not completed yet.",
                    GroupsTotal: groups.Count,
                    GroupsWithCompletedRounds: completedGames);
            }

            return new StageCompletionSnapshot(
                IsComplete: true,
                AlreadyAdvanced: false,
                ProgressLabel: "ready_to_advance",
                Reason: "All scheduled games are completed.",
                GroupsTotal: groups.Count,
                GroupsWithCompletedRounds: groups.Count);
        }

        var incompleteGroups = (await conn.QueryAsync<string>(
            """
            SELECT g.name FROM br_groups g
            WHERE g.stage_id = @stageId
              AND NOT EXISTS (
                  SELECT 1
                  FROM br_lobbies l
                  JOIN br_lobby_groups lg ON lg.lobby_id = l.id
                  JOIN br_games bg ON bg.lobby_id = l.id
                  WHERE lg.group_id = g.id
                  GROUP BY l.id
                  HAVING COUNT(*) FILTER (WHERE bg.status = 'completed') >= @gamesPerLobby
              )
            ORDER BY g.group_order
            """,
            new { stageId, gamesPerLobby })).ToList();

        var groupsWithCompleted = groups.Count - incompleteGroups.Count;

        if (incompleteGroups.Count > 0)
        {
            var progressLabel = totalGames > 0 || hasActiveGame ? "in_progress" : "setup";
            var names = string.Join(", ", incompleteGroups);
            return new StageCompletionSnapshot(
                IsComplete: false,
                AlreadyAdvanced: false,
                ProgressLabel: progressLabel,
                Reason: incompleteGroups.Count == groups.Count
                    ? $"No groups have completed all {gamesPerLobby} games yet."
                    : $"Groups missing completed games: {names}.",
                GroupsTotal: groups.Count,
                GroupsWithCompletedRounds: groupsWithCompleted);
        }

        return new StageCompletionSnapshot(
            IsComplete: true,
            AlreadyAdvanced: false,
            ProgressLabel: "ready_to_advance",
            Reason: $"All groups completed {gamesPerLobby} games per lobby.",
            GroupsTotal: groups.Count,
            GroupsWithCompletedRounds: groups.Count);
    }

    public static StageCompletionSnapshot WithBracketResult(
        StageCompletionSnapshot bracketCore,
        bool alreadyAdvanced)
    {
        if (alreadyAdvanced)
        {
            return bracketCore with
            {
                AlreadyAdvanced = true,
                ProgressLabel = "advanced",
                Reason = bracketCore.Reason ?? "Stage has already been advanced.",
            };
        }

        if (bracketCore.IsComplete)
        {
            return bracketCore with { ProgressLabel = "ready_to_advance" };
        }

        var label = string.Equals(bracketCore.Reason, "No bracket found", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bracketCore.Reason, "No matches found", StringComparison.OrdinalIgnoreCase)
            ? "setup"
            : "in_progress";

        return bracketCore with { ProgressLabel = label };
    }

    public static async Task<bool> IsStageAlreadyAdvancedAsync(
        IDbConnection conn,
        dynamic stage,
        Guid stageId)
    {
        var tournamentId = (Guid)stage.tournament_id;
        var stageOrder = (int)stage.stage_order;

        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM tournament_stages ns
                JOIN stage_participants sp ON sp.stage_id = ns.id
                WHERE ns.tournament_id = @tournamentId
                  AND ns.stage_order = @nextOrder
            )
            """,
            new { tournamentId, nextOrder = stageOrder + 1 });
    }

    public static async Task<bool> HasNextStageAsync(
        IDbConnection conn,
        Guid tournamentId,
        int stageOrder)
        => await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM tournament_stages
                WHERE tournament_id = @tournamentId
                  AND stage_order > @stageOrder
            )
            """,
            new { tournamentId, stageOrder });

    public static async Task<string> EvaluateBracketProgressLabelAsync(
        IDbConnection conn,
        dynamic stage,
        Guid stageId)
    {
        var tournamentId = (Guid)stage.tournament_id;
        var stageOrder = (int)stage.stage_order;
        var hasNextStage = await HasNextStageAsync(conn, tournamentId, stageOrder);
        var alreadyAdvanced = await IsStageAlreadyAdvancedAsync(conn, stage, stageId);
        if (alreadyAdvanced && hasNextStage)
            return "advanced";

        var version = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT id
            FROM brkt_versions
            WHERE stage_id = @stageId
            ORDER BY version_number DESC
            LIMIT 1
            """,
            new { stageId });

        if (version is null)
            return "setup";

        var matchStats = await conn.QuerySingleAsync<(int total, int pending)>(
            """
            SELECT
                COUNT(*)::int AS total,
                COUNT(*) FILTER (WHERE status IS DISTINCT FROM 'completed')::int AS pending
            FROM brkt_matches
            WHERE version_id = @versionId
            """,
            new { versionId = version.Value });

        if (matchStats.total == 0)
            return "setup";

        if (matchStats.pending > 0)
            return "in_progress";

        if (!hasNextStage)
            return "completed";

        return "ready_to_advance";
    }

    public static async Task<(DateTimeOffset? StartDate, DateTimeOffset? EndDate, string Status)> GetTournamentWindowForStageAsync(
        IDbConnection conn,
        Guid stageId,
        IDbTransaction? tx = null)
    {
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT t.start_date, t.end_date, t.status::text AS status
            FROM tournament_stages ts
            JOIN tournaments t ON t.id = ts.tournament_id
            WHERE ts.id = @stageId
            """,
            new { stageId },
            tx);

        if (row is null)
            return (null, null, string.Empty);

        return (
            BrGameRepository.CoerceTimestamp(row.start_date),
            BrGameRepository.CoerceTimestamp(row.end_date),
            (string)row.status);
    }
}
