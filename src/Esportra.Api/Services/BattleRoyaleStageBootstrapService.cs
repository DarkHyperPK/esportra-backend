using System.Data;
using Dapper;
using Esportra.Api.Helpers;

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
                t.max_teams AS "TournamentMaxTeams",
                t.team_size AS "TeamSize"
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
        var teamSize = Math.Max(1, stages.FirstOrDefault()?.TeamSize ?? 1);
        var checkInRequired = await BRSeedEligibility.IsCheckInRequiredAsync(conn, tournamentId, tx);
        var eligibleCount = await CountSeedEligibleUnitsAsync(conn, tx, tournamentId, teamSize == 1, checkInRequired);
        var incomingUnits = ResolveIncomingUnits(stages.FirstOrDefault()?.TournamentMaxTeams, eligibleCount);

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

    public async Task<BattleRoyaleStageLobbiesResult> EnsureStageLobbiesAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        Guid stageId,
        int playersPerLobby = 100)
    {
        var stage = await LoadStageRowAsync(conn, tx, stageId);
        if (stage is null)
        {
            return new BattleRoyaleStageLobbiesResult(stageId, false, "Battle royale stage not found.");
        }

        var format = BattleRoyaleConfigResolver.ResolveFormat(stage.Config);
        if (format is BattleRoyaleConfigResolver.BrStageFormat.GroupRotation)
        {
            return new BattleRoyaleStageLobbiesResult(
                stageId,
                false,
                "Round-robin stages use the Schedule tab to create matches.");
        }

        var groupCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*)::int FROM public.br_groups WHERE stage_id = @stageId",
            new { stageId },
            tx);

        if (groupCount == 0)
        {
            return new BattleRoyaleStageLobbiesResult(
                stageId,
                false,
                "Initialize groups before creating matches.");
        }

        var assignedCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)::int
            FROM public.br_group_teams gt
            JOIN public.br_groups g ON g.id = gt.group_id
            WHERE g.stage_id = @stageId
            """,
            new { stageId },
            tx);

        if (assignedCount == 0)
        {
            return new BattleRoyaleStageLobbiesResult(
                stageId,
                false,
                "Seed participants before creating matches.");
        }

        var seedingError = await ValidateSeedingCompleteAsync(conn, tx, stage, assignedCount);
        if (seedingError is not null)
        {
            return new BattleRoyaleStageLobbiesResult(stageId, false, seedingError);
        }

        if (format == BattleRoyaleConfigResolver.BrStageFormat.SingleLobby)
        {
            var lobbySize = await conn.ExecuteScalarAsync<int>(
                """
                SELECT COALESCE(MAX(lobby_size), 0)
                FROM public.br_groups
                WHERE stage_id = @stageId
                """,
                new { stageId },
                tx);

            var teamSize = Math.Max(1, stage.TeamSize ?? 1);
            var playerCount = assignedCount * teamSize;

            if (lobbySize > 0 && assignedCount > lobbySize)
            {
                return new BattleRoyaleStageLobbiesResult(
                    stageId,
                    false,
                    $"{assignedCount} participants exceed the lobby size of {lobbySize}. Use Group qualifiers to split the field.");
            }

            if (playerCount > playersPerLobby)
            {
                return new BattleRoyaleStageLobbiesResult(
                    stageId,
                    false,
                    $"{playerCount} players exceed the {playersPerLobby}-player lobby cap. Use Group qualifiers to split the field.");
            }
        }
        else if (format is BattleRoyaleConfigResolver.BrStageFormat.StaticGroups
            or BattleRoyaleConfigResolver.BrStageFormat.MultiLobbyCut)
        {
            var overcrowdedGroups = (await conn.QueryAsync<(string Name, int Assigned, int LobbySize)>(
                """
                SELECT g.name, COUNT(gt.id)::int AS assigned, g.lobby_size
                FROM public.br_groups g
                LEFT JOIN public.br_group_teams gt ON gt.group_id = g.id
                WHERE g.stage_id = @stageId
                GROUP BY g.id, g.name, g.lobby_size
                HAVING COUNT(gt.id) > g.lobby_size
                """,
                new { stageId },
                tx)).ToList();

            if (overcrowdedGroups.Count > 0)
            {
                var first = overcrowdedGroups[0];
                return new BattleRoyaleStageLobbiesResult(
                    stageId,
                    false,
                    $"{first.Name} has {first.Assigned} participants but lobby size is {first.LobbySize}. Re-seed or add more groups.");
            }
        }

        var existingLobbies = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*)::int FROM br_lobbies WHERE stage_id = @stageId",
            new { stageId },
            tx);

        if (existingLobbies > 0)
        {
            return new BattleRoyaleStageLobbiesResult(stageId, false, null);
        }

        await EnsureInitialLobbiesAsync(conn, tx, stage, groupCount, format);

        _logger.LogInformation(
            "Generated BR lobbies for stage {StageId} ({StageName})",
            stage.Id,
            stage.Name);

        return new BattleRoyaleStageLobbiesResult(stageId, true, null);
    }

    private static async Task<BattleRoyaleStageRow?> LoadStageRowAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        Guid stageId)
    {
        return await conn.QuerySingleOrDefaultAsync<BattleRoyaleStageRow>(
            """
            SELECT
                ts.id AS "Id",
                ts.name AS "Name",
                ts.stage_order AS "StageOrder",
                ts.capacity AS "Capacity",
                ts.advancement_count AS "AdvancementCount",
                ts.config::text AS "Config",
                t.max_teams AS "TournamentMaxTeams",
                t.team_size AS "TeamSize"
            FROM public.tournament_stages ts
            JOIN public.tournaments t ON t.id = ts.tournament_id
            WHERE ts.id = @stageId
              AND ts.format = 'battle_royale'
            """,
            new { stageId },
            tx);
    }

    private static async Task<string?> ValidateSeedingCompleteAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        BattleRoyaleStageRow stage,
        int assignedCount)
    {
        var tournamentId = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT tournament_id FROM public.tournament_stages WHERE id = @stageId",
            new { stageId = stage.Id },
            tx);

        if (tournamentId is null)
            return "Battle royale stage not found.";

        var teamSize = Math.Max(1, stage.TeamSize ?? 1);
        var isSolo = teamSize == 1;

        var emptyGroups = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)::int
            FROM public.br_groups g
            WHERE g.stage_id = @stageId
              AND NOT EXISTS (
                  SELECT 1 FROM public.br_group_teams gt WHERE gt.group_id = g.id
              )
            """,
            new { stageId = stage.Id },
            tx);

        if (emptyGroups > 0)
        {
            return "All groups must have participants before creating matches.";
        }

        var eligibleCount = await CountSeedEligibleUnitsAsync(
            conn,
            tx,
            tournamentId.Value,
            isSolo,
            await BRSeedEligibility.IsCheckInRequiredAsync(conn, tournamentId.Value, tx));
        var expectedIncoming = await ComputeIncomingUnitsForStageAsync(conn, tx, stage, eligibleCount);

        if (expectedIncoming > 0 && assignedCount < expectedIncoming)
        {
            return $"Seed all participants before creating matches ({assignedCount}/{expectedIncoming} assigned).";
        }

        if (expectedIncoming <= 0 && eligibleCount > 0 && assignedCount < eligibleCount && stage.StageOrder == 1)
        {
            return $"Seed all participants before creating matches ({assignedCount}/{eligibleCount} assigned).";
        }

        return null;
    }

    private static async Task<int> CountSeedEligibleUnitsAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        Guid tournamentId,
        bool isSolo,
        bool checkInRequired)
    {
        var statuses = BRSeedEligibility.ResolveStatuses(checkInRequired);

        if (isSolo)
        {
            return await conn.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*)::int
                FROM public.tournament_participants
                WHERE tournament_id = @tournamentId
                  AND status::text = ANY(@statuses)
                """,
                new { tournamentId, statuses },
                tx);
        }

        return await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(DISTINCT team_id)::int
            FROM public.tournament_participants
            WHERE tournament_id = @tournamentId
              AND team_id IS NOT NULL
              AND status::text = ANY(@statuses)
            """,
            new { tournamentId, statuses },
            tx);
    }

    private static async Task<int> ComputeIncomingUnitsForStageAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        BattleRoyaleStageRow stage,
        int eligibleCount)
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
                t.max_teams AS "TournamentMaxTeams",
                t.team_size AS "TeamSize"
            FROM public.tournament_stages ts
            JOIN public.tournaments t ON t.id = ts.tournament_id
            WHERE ts.tournament_id = (
                SELECT tournament_id FROM public.tournament_stages WHERE id = @stageId
            )
              AND ts.format = 'battle_royale'
            ORDER BY ts.stage_order
            """,
            new { stageId = stage.Id },
            tx)).ToList();

        var incomingUnits = ResolveIncomingUnits(stages.FirstOrDefault()?.TournamentMaxTeams, eligibleCount);
        if (incomingUnits <= 0 && eligibleCount > 0)
            incomingUnits = eligibleCount;

        foreach (var current in stages)
        {
            if (current.Id == stage.Id)
                return incomingUnits;

            var groupCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*)::int FROM public.br_groups WHERE stage_id = @stageId",
                new { stageId = current.Id },
                tx);

            var groupCountForFlow = groupCount > 0 ? groupCount : 1;
            incomingUnits = current.AdvancementCount is > 0
                ? current.AdvancementCount.Value * groupCountForFlow
                : Math.Max(0, incomingUnits);
        }

        return incomingUnits;
    }

    private static async Task EnsureInitialLobbiesAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        BattleRoyaleStageRow stage,
        int groupCount,
        BattleRoyaleConfigResolver.BrStageFormat format)
    {
        if (format is BattleRoyaleConfigResolver.BrStageFormat.GroupRotation)
        {
            return;
        }

        var existingLobbies = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*)::int FROM br_lobbies WHERE stage_id = @stageId",
            new { stageId = stage.Id },
            tx);
        if (existingLobbies > 0)
            return;

        var groupIds = (await conn.QueryAsync<(Guid Id, int Order)>(
            """
            SELECT id, group_order
            FROM br_groups
            WHERE stage_id = @stageId
            ORDER BY group_order
            """,
            new { stageId = stage.Id },
            tx)).ToList();

        if (format == BattleRoyaleConfigResolver.BrStageFormat.SingleLobby && groupIds.Count > 0)
        {
            var lobbyId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO br_lobbies (stage_id, wave_number, lobby_index)
                VALUES (@stageId, 1, 0)
                RETURNING id
                """,
                new { stageId = stage.Id },
                tx);

            await conn.ExecuteAsync(
                "INSERT INTO br_lobby_groups (lobby_id, group_id) VALUES (@lobbyId, @groupId) ON CONFLICT DO NOTHING",
                new { lobbyId, groupId = groupIds[0].Id },
                tx);
        }
        else if (format is BattleRoyaleConfigResolver.BrStageFormat.StaticGroups
            or BattleRoyaleConfigResolver.BrStageFormat.MultiLobbyCut)
        {
            foreach (var group in groupIds)
            {
                var lobbyId = await conn.QuerySingleAsync<Guid>(
                    """
                    INSERT INTO br_lobbies (stage_id, wave_number, lobby_index)
                    VALUES (@stageId, 1, @lobbyIndex)
                    RETURNING id
                    """,
                    new { stageId = stage.Id, lobbyIndex = group.Order - 1 },
                    tx);

                await conn.ExecuteAsync(
                    "INSERT INTO br_lobby_groups (lobby_id, group_id) VALUES (@lobbyId, @groupId) ON CONFLICT DO NOTHING",
                    new { lobbyId, groupId = group.Id },
                    tx);
            }
        }

        var tournamentSettings = await conn.QuerySingleOrDefaultAsync<object>(
            """
            SELECT t.settings
            FROM tournament_stages ts
            JOIN tournaments t ON t.id = ts.tournament_id
            WHERE ts.id = @stageId
            """,
            new { stageId = stage.Id },
            tx);

        await BrGameMaterializer.MaterializeStageGamesAsync(
            conn,
            stage.Id,
            tournamentSettings: tournamentSettings,
            stageConfig: stage.Config,
            tx: tx);
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

    private static int ResolveIncomingUnits(int? tournamentMaxTeams, int eligibleCount)
    {
        var cap = tournamentMaxTeams ?? 0;
        if (eligibleCount > 0)
            return cap > 0 ? Math.Min(cap, eligibleCount) : eligibleCount;

        return Math.Max(0, cap);
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
        int? TournamentMaxTeams,
        int? TeamSize);
}

public sealed record BattleRoyaleStageBootstrapResult(Guid StageId, int GroupCount, bool Created);

public sealed record BattleRoyaleStageLobbiesResult(Guid StageId, bool Created, string? Error);
