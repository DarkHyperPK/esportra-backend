using System.Data;
using Dapper;

namespace Esportra.Api.Services;

public sealed class BattleRoyaleStageBootstrapService
{
    private readonly ILogger<BattleRoyaleStageBootstrapService> _logger;

    public BattleRoyaleStageBootstrapService(ILogger<BattleRoyaleStageBootstrapService> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<BattleRoyaleStageBootstrapResult>> EnsureTournamentGroupsAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        Guid tournamentId)
    {
        var stages = (await conn.QueryAsync<BattleRoyaleStageRow>(
            """
            SELECT
                ts.id AS "Id",
                ts.name AS "Name",
                ts.stage_order AS "StageOrder",
                ts.capacity AS "Capacity",
                ts.advancement_count AS "AdvancementCount",
                ts.config::text AS "Config",
                t.max_teams AS "TournamentMaxTeams"
            FROM public.tournament_stages ts
            JOIN public.tournaments t ON t.id = ts.tournament_id
            WHERE ts.tournament_id = @tournamentId
              AND ts.format = 'battle_royale'
            ORDER BY ts.stage_order
            FOR UPDATE OF ts
            """,
            new { tournamentId },
            tx)).ToList();

        var results = new List<BattleRoyaleStageBootstrapResult>(stages.Count);
        var incomingUnits = stages.FirstOrDefault()?.TournamentMaxTeams ?? 0;

        foreach (var stage in stages)
        {
            var result = await EnsureStageGroupsAsync(conn, tx, stage, incomingUnits);
            results.Add(result);

            var groupCountForFlow = result.GroupCount > 0 ? result.GroupCount : 1;
            incomingUnits = stage.AdvancementCount is > 0
                ? stage.AdvancementCount.Value * groupCountForFlow
                : Math.Max(0, stage.Capacity ?? incomingUnits);
        }

        return results;
    }

    public async Task<IReadOnlyList<BattleRoyaleStageBootstrapResult>> EnsureStageGroupsAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        Guid stageId)
    {
        var tournamentId = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT tournament_id FROM public.tournament_stages WHERE id = @stageId AND format = 'battle_royale'",
            new { stageId },
            tx);

        if (tournamentId is null)
        {
            return Array.Empty<BattleRoyaleStageBootstrapResult>();
        }

        var results = await EnsureTournamentGroupsAsync(conn, tx, tournamentId.Value);
        return results.Where(result => result.StageId == stageId).ToArray();
    }

    private async Task<BattleRoyaleStageBootstrapResult> EnsureStageGroupsAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        BattleRoyaleStageRow stage,
        int incomingUnits)
    {
        var existingGroupCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM public.br_groups WHERE stage_id = @stageId",
            new { stageId = stage.Id },
            tx);

        if (existingGroupCount > 0)
        {
            return new BattleRoyaleStageBootstrapResult(stage.Id, existingGroupCount, false);
        }

        var format = BattleRoyaleConfigResolver.ResolveFormat(stage.Config);
        var lobbySize = ResolveLobbySize(stage, incomingUnits);
        var targetGroupCount = ResolveGroupCount(stage, incomingUnits, lobbySize, format);

        for (var i = 0; i < targetGroupCount; i++)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.br_groups (stage_id, name, group_order, lobby_size)
                VALUES (@stageId, @name, @groupOrder, @lobbySize)
                ON CONFLICT (stage_id, group_order) DO NOTHING
                """,
                new
                {
                    stageId = stage.Id,
                    name = targetGroupCount == 1 ? "Main Lobby" : GenerateGroupName(i),
                    groupOrder = i + 1,
                    lobbySize,
                },
                tx);
        }

        _logger.LogInformation(
            "Bootstrapped {GroupCount} BR group(s) for stage {StageId} ({StageName})",
            targetGroupCount,
            stage.Id,
            stage.Name);

        return new BattleRoyaleStageBootstrapResult(stage.Id, targetGroupCount, true);
    }

    private static int ResolveLobbySize(BattleRoyaleStageRow stage, int incomingUnits)
    {
        if (stage.Capacity is > 0)
        {
            return stage.Capacity.Value;
        }

        if (incomingUnits > 0)
        {
            return incomingUnits;
        }

        return Math.Max(1, stage.TournamentMaxTeams ?? 1);
    }

    private static int ResolveGroupCount(
        BattleRoyaleStageRow stage,
        int incomingUnits,
        int lobbySize,
        BattleRoyaleConfigResolver.BrStageFormat format)
    {
        if (format == BattleRoyaleConfigResolver.BrStageFormat.SingleLobby)
            return 1;

        if (incomingUnits <= 0 || lobbySize <= 0)
            return 1;

        if (format == BattleRoyaleConfigResolver.BrStageFormat.GroupRotation)
        {
            var evenGroups = Math.Max(2, (int)Math.Ceiling(incomingUnits / (double)lobbySize));
            if (evenGroups % 2 != 0)
                evenGroups += 1;
            return evenGroups;
        }

        if (stage.AdvancementCount is null or <= 0 && format == BattleRoyaleConfigResolver.BrStageFormat.StaticGroups)
            return Math.Max(1, (int)Math.Ceiling(incomingUnits / (double)lobbySize));

        if (stage.AdvancementCount is null or <= 0)
            return 1;

        return Math.Max(1, (int)Math.Ceiling(incomingUnits / (double)lobbySize));
    }

    private static string GenerateGroupName(int index)
    {
        var name = "";
        var n = index;
        do
        {
            name = (char)('A' + (n % 26)) + name;
            n = n / 26 - 1;
        } while (n >= 0);

        return $"Group {name}";
    }

    private sealed record BattleRoyaleStageRow(
        Guid Id,
        string Name,
        int StageOrder,
        int? Capacity,
        int? AdvancementCount,
        string? Config,
        int? TournamentMaxTeams);
}

public sealed record BattleRoyaleStageBootstrapResult(Guid StageId, int GroupCount, bool Created);
