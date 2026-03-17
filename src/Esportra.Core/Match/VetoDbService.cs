using Dapper;
using Esportra.Contracts.Database;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.Json;

namespace Esportra.Core.Match;

/// <summary>DB-backed veto operations with FSM validation and optimistic locking.</summary>
public sealed class VetoDbService(IDbConnectionFactory db, ILogger<VetoDbService> logger)
{
    // ── Fetch ────────────────────────────────────────────────────────────────

    public async Task<MatchMapVeto?> GetAsync(Guid matchId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleOrDefaultAsync(@"
            SELECT id, match_id, tournament_id,
                   team1_id, team2_id, best_of, status,
                   current_team_id, current_action, current_action_number,
                   team1_banned_maps, team2_banned_maps,
                   team1_picked_maps, team2_picked_maps,
                   selected_map_id::text as selected_map_id, selected_map_pool,
                   started_at, completed_at, game,
                   team1_link_token, team2_link_token,
                   turn_started_at, turn_duration_seconds
            FROM public.match_map_vetos
            WHERE match_id = @matchId",
            new { matchId });

        if (row is null) return null;

        return MapRow(row);
    }

    /// <summary>Fetch by veto primary key (id) instead of match_id.</summary>
    public async Task<MatchMapVeto?> GetByIdAsync(Guid vetoId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync(@"
            SELECT id, match_id, tournament_id,
                   team1_id, team2_id, best_of, status,
                   current_team_id, current_action, current_action_number,
                   team1_banned_maps, team2_banned_maps,
                   team1_picked_maps, team2_picked_maps,
                   selected_map_id::text as selected_map_id, selected_map_pool,
                   started_at, completed_at, game,
                   team1_link_token, team2_link_token,
                   turn_started_at, turn_duration_seconds
            FROM public.match_map_vetos
            WHERE id = @vetoId",
            new { vetoId });
        return row is null ? null : MapRow(row);
    }

    // ── Authorization helpers ─────────────────────────────────────────────────

    /// <summary>Check if user is captain/owner of a specific team.</summary>
    public async Task<bool> IsTeamCaptainAsync(Guid userId, Guid teamId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var isCaptain = await conn.QuerySingleOrDefaultAsync<bool>(@"
            SELECT EXISTS(
                SELECT 1 FROM team_members
                WHERE team_id = @teamId AND user_id = @userId
                  AND role::text IN ('captain','owner') AND is_active = TRUE
            ) OR EXISTS(
                SELECT 1 FROM teams WHERE id = @teamId AND owner_id = @userId
            )",
            new { teamId, userId });
        return isCaptain;
    }

    /// <summary>Check if user is the tournament organizer.</summary>
    public async Task<bool> IsOrganizerAsync(Guid userId, Guid tournamentId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<bool>(@"
            SELECT EXISTS(
                SELECT 1 FROM tournaments WHERE id = @tournamentId AND organizer_id = @userId
            ) OR EXISTS(
                SELECT 1 FROM organization_staff os
                JOIN tournaments t ON t.organization_id = os.organization_id
                WHERE t.id = @tournamentId AND os.user_id = @userId
                  AND os.role IN ('owner','admin') AND os.status = 'active'
            )",
            new { tournamentId, userId });
    }

    // ── Initialize veto ──────────────────────────────────────────────────────

    public async Task<MatchMapVeto> InitAsync(
        Guid matchId, Guid tournamentId,
        Guid? team1Id, Guid? team2Id,
        int bestOf, string game = "valorant",
        CancellationToken ct = default)
    {
        logger.LogInformation("InitAsync: matchId={MatchId}, tournamentId={TournamentId}, bestOf={BestOf}, team1={T1}, team2={T2}",
            matchId, tournamentId, bestOf, team1Id, team2Id);

        var firstStep = VetoSequences.GetStep(bestOf, 1)
            ?? throw new InvalidOperationException("No veto sequence for bestOf=" + bestOf);

        Guid? firstTeamId = firstStep.Team == "T1" ? team1Id : team2Id;

        // Generate cryptographic tokens for team links (S5)
        var team1Token = GenerateCryptoToken();
        var team2Token = GenerateCryptoToken();

        using var conn = db.CreateConnection();

        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO public.match_map_vetos
                (id, match_id, tournament_id, team1_id, team2_id, best_of, status,
                 current_team_id, current_action, current_action_number,
                 team1_banned_maps, team2_banned_maps,
                 team1_picked_maps, team2_picked_maps,
                 started_at, game, team1_link_token, team2_link_token,
                 turn_started_at)
            VALUES
                (@id, @match_id, @tournament_id, @team1_id, @team2_id, @best_of, 'in_progress',
                 @current_team_id, @current_action, @action_number,
                 '{}', '{}',
                 '[]'::jsonb, '[]'::jsonb,
                 now(), @game, @team1_token, @team2_token, now())
            ON CONFLICT (match_id) DO UPDATE SET
                best_of = @best_of,
                status = 'in_progress',
                current_team_id = @current_team_id,
                current_action = @current_action,
                current_action_number = @action_number,
                team1_banned_maps = '{}',
                team2_banned_maps = '{}',
                team1_picked_maps = '[]'::jsonb,
                team2_picked_maps = '[]'::jsonb,
                selected_map_id = null,
                completed_at = null,
                started_at = now(),
                turn_started_at = now(),
                team1_link_token = COALESCE(match_map_vetos.team1_link_token, @team1_token),
                team2_link_token = COALESCE(match_map_vetos.team2_link_token, @team2_token)",
            new
            {
                id,
                match_id       = matchId,
                tournament_id  = tournamentId,
                team1_id       = team1Id,
                team2_id       = team2Id,
                best_of        = bestOf,
                current_team_id = firstTeamId,
                current_action = firstStep.Action,
                action_number  = firstStep.ActionNumber,
                game,
                team1_token    = team1Token,
                team2_token    = team2Token,
            });

        return (await GetAsync(matchId, ct))!;
    }

    // ── Ban (with FSM + optimistic lock) ─────────────────────────────────────

    public async Task<MatchMapVeto> BanMapAsync(
        Guid matchId, string mapId, Guid userId, CancellationToken ct = default)
    {
        var veto = await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException("Veto not found");

        // D2: Server-side FSM validation
        ValidateAction(veto, VetoEvent.BanMap, mapId);

        // S1: Verify user is captain of the current team
        await AssertIsCaptainOfCurrentTeam(userId, veto, ct);

        bool isTeam1 = veto.CurrentTeamId == veto.Team1Id;
        string col   = isTeam1 ? "team1_banned_maps" : "team2_banned_maps";

        var next = VetoEngine.NextAction(veto.BestOf, veto.CurrentActionNumber);

        // D4: Optimistic lock — only update if action_number hasn't changed
        await AdvanceOrCompleteAsync(matchId, veto, next, col, mapId, null);

        return (await GetAsync(matchId, ct))!;
    }

    // ── Pick (with FSM + optimistic lock) ────────────────────────────────────

    public async Task<MatchMapVeto> PickMapAsync(
        Guid matchId, string mapId, Guid userId, CancellationToken ct = default)
    {
        var veto = await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException("Veto not found");

        ValidateAction(veto, VetoEvent.PickMap, mapId);
        await AssertIsCaptainOfCurrentTeam(userId, veto, ct);

        bool isTeam1 = veto.CurrentTeamId == veto.Team1Id;
        string col   = isTeam1 ? "team1_picked_maps" : "team2_picked_maps";

        var next = VetoEngine.NextAction(veto.BestOf, veto.CurrentActionNumber);
        await AdvanceOrCompleteAsync(matchId, veto, next, col, mapId, null, isPick: true);

        return (await GetAsync(matchId, ct))!;
    }

    // ── Pick side (with FSM + optimistic lock) ───────────────────────────────

    public async Task<MatchMapVeto> PickSideAsync(
        Guid matchId, string mapId, string side, Guid userId, CancellationToken ct = default)
    {
        var veto = await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException("Veto not found");

        ValidateAction(veto, VetoEvent.PickSide, mapId);
        await AssertIsCaptainOfCurrentTeam(userId, veto, ct);

        using var conn = db.CreateConnection();

        // B3: COALESCE to prevent jsonb_agg NULL when array is empty
        // Update side on existing picked entries
        var updated = await conn.ExecuteAsync(@"
            UPDATE public.match_map_vetos
               SET team1_picked_maps = COALESCE((
                     SELECT jsonb_agg(
                       CASE WHEN m->>'map_id' = @mapId THEN m || jsonb_build_object('side', @side) ELSE m END
                     ) FROM jsonb_array_elements(team1_picked_maps) AS m
                   ), '[]'::jsonb),
                   team2_picked_maps = COALESCE((
                     SELECT jsonb_agg(
                       CASE WHEN m->>'map_id' = @mapId THEN m || jsonb_build_object('side', @side) ELSE m END
                     ) FROM jsonb_array_elements(team2_picked_maps) AS m
                   ), '[]'::jsonb)
             WHERE match_id = @matchId
               AND current_action_number = @expectedAction",
            new { matchId, mapId, side, expectedAction = veto.CurrentActionNumber });

        if (updated == 0)
            throw new InvalidOperationException("CONFLICT: veto state changed (optimistic lock)");

        // Decider fix: if the map wasn't in either team's picks (leftover map),
        // append it to the current team's picks so the side is stored.
        var newEntry = System.Text.Json.JsonSerializer.Serialize(
            new[] { new { map_id = mapId, side } });

        var isTeam1 = veto.CurrentTeamId == veto.Team1Id;
        var appendSql = isTeam1
            ? @"UPDATE public.match_map_vetos
                   SET team1_picked_maps = team1_picked_maps || @entry::jsonb
                 WHERE match_id = @matchId
                   AND NOT EXISTS (
                       SELECT 1 FROM jsonb_array_elements(team1_picked_maps) m
                        WHERE m->>'map_id' = @mapId
                   )"
            : @"UPDATE public.match_map_vetos
                   SET team2_picked_maps = team2_picked_maps || @entry::jsonb
                 WHERE match_id = @matchId
                   AND NOT EXISTS (
                       SELECT 1 FROM jsonb_array_elements(team2_picked_maps) m
                        WHERE m->>'map_id' = @mapId
                   )";
        await conn.ExecuteAsync(appendSql, new { matchId, mapId, entry = newEntry });

        var next = VetoEngine.NextAction(veto.BestOf, veto.CurrentActionNumber);
        await SetNextActionAsync(matchId, veto, next);

        return (await GetAsync(matchId, ct))!;
    }

    // ── Reset ────────────────────────────────────────────────────────────────

    public async Task ResetAsync(Guid matchId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        // Delete game rows created from the previous veto so stale data doesn't survive
        await conn.ExecuteAsync(
            "DELETE FROM public.brkt_match_games WHERE match_id = @matchId",
            new { matchId });

        await conn.ExecuteAsync(@"
            UPDATE public.match_map_vetos
               SET status = 'pending',
                   current_team_id = null,
                   current_action = null,
                   current_action_number = 0,
                   team1_banned_maps = '{}',
                   team2_banned_maps = '{}',
                   team1_picked_maps = '[]'::jsonb,
                   team2_picked_maps = '[]'::jsonb,
                   selected_map_id = null,
                   started_at = null,
                   completed_at = null,
                   turn_started_at = null
             WHERE match_id = @matchId",
            new { matchId });
    }

    // ── Internal: FSM validation ─────────────────────────────────────────────

    private static void ValidateAction(MatchMapVeto veto, VetoEvent ev, string? mapId)
    {
        var state = VetoEngine.DeriveState(veto);

        // Check state matches expected action type
        if (ev == VetoEvent.BanMap && state != VetoState.Ban)
            throw new InvalidOperationException($"INVALID_STATE: expected Ban, got {state}");
        if (ev == VetoEvent.PickMap && state != VetoState.Pick)
            throw new InvalidOperationException($"INVALID_STATE: expected Pick, got {state}");
        if (ev == VetoEvent.PickSide && state != VetoState.PickSide)
            throw new InvalidOperationException($"INVALID_STATE: expected PickSide, got {state}");

        if (state == VetoState.Complete)
            throw new InvalidOperationException("INVALID_STATE: veto already completed");

        // Check map not already used
        if (mapId is not null)
        {
            bool isBanned = veto.Team1BannedMaps.Contains(mapId) || veto.Team2BannedMaps.Contains(mapId);
            bool isPicked = veto.Team1PickedMaps.Any(p => p.MapId == mapId)
                         || veto.Team2PickedMaps.Any(p => p.MapId == mapId);

            if (isBanned)
                throw new InvalidOperationException("MAP_ALREADY_USED: map is already banned");
            if (ev != VetoEvent.PickSide && isPicked)
                throw new InvalidOperationException("MAP_ALREADY_USED: map is already picked");
        }
    }

    private async Task AssertIsCaptainOfCurrentTeam(Guid userId, MatchMapVeto veto, CancellationToken ct)
    {
        if (veto.CurrentTeamId is null)
            throw new InvalidOperationException("No current team assigned");

        // Allow organizer to act on behalf of teams
        var isOrg = await IsOrganizerAsync(userId, veto.TournamentId, ct);
        logger.LogInformation("AssertCaptain: userId={UserId}, tournamentId={TournamentId}, isOrganizer={IsOrg}, currentTeamId={TeamId}",
            userId, veto.TournamentId, isOrg, veto.CurrentTeamId);
        if (isOrg) return;

        var isCap = await IsTeamCaptainAsync(userId, veto.CurrentTeamId.Value, ct);
        logger.LogInformation("AssertCaptain: isCaptain={IsCap} for team={TeamId}", isCap, veto.CurrentTeamId);
        if (!isCap)
            throw new UnauthorizedAccessException("NOT_YOUR_TURN: you are not captain of the acting team");
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    private async Task AdvanceOrCompleteAsync(
        Guid matchId, MatchMapVeto veto,
        (string? Action, string? TeamSide)? next,
        string arrayCol, string mapId, string? side,
        bool isPick = false)
    {
        using var conn = db.CreateConnection();

        // D4: Optimistic lock — only update if current_action_number matches
        int updated;
        if (isPick)
        {
            updated = await conn.ExecuteAsync($@"
                UPDATE public.match_map_vetos
                   SET {arrayCol} = {arrayCol} || @entry::jsonb
                 WHERE match_id = @matchId
                   AND current_action_number = @expectedAction",
                new
                {
                    matchId,
                    entry = JsonSerializer.Serialize(new { map_id = mapId, side = (string?)null }),
                    expectedAction = veto.CurrentActionNumber,
                });
        }
        else
        {
            updated = await conn.ExecuteAsync($@"
                UPDATE public.match_map_vetos
                   SET {arrayCol} = array_append({arrayCol}, @mapId)
                 WHERE match_id = @matchId
                   AND current_action_number = @expectedAction",
                new { matchId, mapId, expectedAction = veto.CurrentActionNumber });
        }

        if (updated == 0)
            throw new InvalidOperationException("CONFLICT: veto state changed (optimistic lock)");

        await SetNextActionAsync(matchId, veto, next);
    }

    private async Task SetNextActionAsync(
        Guid matchId, MatchMapVeto veto,
        (string? Action, string? TeamSide)? next)
    {
        using var conn = db.CreateConnection();

        if (next is null)
        {
            // D5: Auto-set selected_map_id for BO1 (last remaining unpicked/unbanned map)
            string? selectedMapSql = null;
            if (veto.BestOf == 1)
                selectedMapSql = ", selected_map_id = (SELECT unnest(selected_map_pool) EXCEPT SELECT unnest(team1_banned_maps) EXCEPT SELECT unnest(team2_banned_maps) LIMIT 1)::uuid";

            await conn.ExecuteAsync($@"
                UPDATE public.match_map_vetos
                   SET status = 'completed',
                       current_team_id = null,
                       current_action = null,
                       completed_at = now()
                       {selectedMapSql ?? ""}
                 WHERE match_id = @matchId",
                new { matchId });

            // Create brkt_match_games rows from the finalized veto
            await CreateMatchGamesFromVetoAsync(matchId, veto);
        }
        else
        {
            Guid? nextTeamId = next.Value.TeamSide == "T1" ? veto.Team1Id : veto.Team2Id;
            int nextActionNumber = veto.CurrentActionNumber + 1;

            await conn.ExecuteAsync(@"
                UPDATE public.match_map_vetos
                   SET current_team_id = @nextTeamId,
                       current_action = @action,
                       current_action_number = @actionNumber,
                       status = 'in_progress',
                       turn_started_at = now()
                 WHERE match_id = @matchId",
                new
                {
                    matchId,
                    nextTeamId,
                    action       = next.Value.Action,
                    actionNumber = nextActionNumber,
                });
        }
    }

    /// <summary>
    /// After veto completes, create brkt_match_games rows for each game in the series
    /// with resolved map_id and map_name. This is the single source of truth for
    /// which maps are played in which order.
    /// </summary>
    private async Task CreateMatchGamesFromVetoAsync(Guid matchId, MatchMapVeto veto)
    {
        try
        {
            using var conn = db.CreateConnection();

            // Re-fetch to get the final state (including selected_map_id for BO1)
            var finalVeto = await conn.QuerySingleOrDefaultAsync<dynamic>(@"
                SELECT team1_picked_maps, team2_picked_maps, selected_map_id::text as selected_map_id,
                       team1_banned_maps, team2_banned_maps, selected_map_pool, best_of
                FROM public.match_map_vetos
                WHERE match_id = @matchId", new { matchId });

            if (finalVeto is null) return;

            int bestOf = Convert.ToInt32(finalVeto.best_of ?? 1);
            PickedMap[] t1Picked = ParsePicked(finalVeto.team1_picked_maps);
            PickedMap[] t2Picked = ParsePicked(finalVeto.team2_picked_maps);
            string? selectedMapId = (string?)finalVeto.selected_map_id;

            // For BO3/BO5: compute decider map if selected_map_id not set
            // Decider = pool minus all bans and picks
            if (selectedMapId is null && bestOf > 1)
            {
                string[] pool = ParseStringArray(finalVeto.selected_map_pool);
                string[] bans1 = ParseStringArray(finalVeto.team1_banned_maps);
                string[] bans2 = ParseStringArray(finalVeto.team2_banned_maps);
                var picks = new HashSet<string>(
                    t1Picked.Select(p => p.MapId).Concat(t2Picked.Select(p => p.MapId)));
                var allExcluded = new HashSet<string>(bans1.Concat(bans2));
                allExcluded.UnionWith(picks);

                selectedMapId = pool.FirstOrDefault(m => !allExcluded.Contains(m));
                if (selectedMapId is not null)
                    logger.LogInformation("Computed decider map for match {MatchId}: {MapId}", matchId, selectedMapId);
            }

            // Build ordered game map list based on veto sequence
            var gameMapIds = new List<string>();

            if (bestOf == 1)
            {
                // BO1: the picked map or selected_map_id (last remaining)
                if (t1Picked.Length > 0)
                    gameMapIds.Add(t1Picked[0].MapId);
                else if (selectedMapId is not null)
                    gameMapIds.Add(selectedMapId);
            }
            else if (bestOf == 3)
            {
                // BO3: T1 pick, T2 pick, decider (selected_map_id)
                if (t1Picked.Length > 0) gameMapIds.Add(t1Picked[0].MapId);
                if (t2Picked.Length > 0) gameMapIds.Add(t2Picked[0].MapId);
                if (selectedMapId is not null) gameMapIds.Add(selectedMapId);
            }
            else if (bestOf == 5)
            {
                // BO5: T1 pick, T2 pick, T1 pick, T2 pick, decider
                if (t1Picked.Length > 0) gameMapIds.Add(t1Picked[0].MapId);
                if (t2Picked.Length > 0) gameMapIds.Add(t2Picked[0].MapId);
                if (t1Picked.Length > 1) gameMapIds.Add(t1Picked[1].MapId);
                if (t2Picked.Length > 1) gameMapIds.Add(t2Picked[1].MapId);
                if (selectedMapId is not null) gameMapIds.Add(selectedMapId);
            }

            if (gameMapIds.Count == 0)
            {
                logger.LogWarning("Veto completed for match {MatchId} but no maps resolved", matchId);
                return;
            }

            // Resolve map names in bulk
            var mapNames = (await conn.QueryAsync<dynamic>(@"
                SELECT id::text as id, map_name
                FROM public.game_maps
                WHERE id::text = ANY(@ids)",
                new { ids = gameMapIds.ToArray() })).ToDictionary(
                    m => (string)m.id,
                    m => (string)m.map_name);

            // Insert game rows (idempotent via ON CONFLICT)
            for (int i = 0; i < gameMapIds.Count; i++)
            {
                var mapId = gameMapIds[i];
                mapNames.TryGetValue(mapId, out var mapName);

                await conn.ExecuteAsync(@"
                    INSERT INTO public.brkt_match_games (match_id, game_number, map_id, map_name, status)
                    VALUES (@matchId, @gameNumber, @mapId::uuid, @mapName, 'pending')
                    ON CONFLICT (match_id, game_number) DO UPDATE
                    SET map_id = @mapId::uuid, map_name = @mapName",
                    new { matchId, gameNumber = i + 1, mapId, mapName });
            }

            logger.LogInformation(
                "Created {Count} match game(s) for match {MatchId} from veto: {Maps}",
                gameMapIds.Count, matchId,
                string.Join(", ", gameMapIds.Select((id, i) =>
                    $"Game {i + 1}: {(mapNames.TryGetValue(id, out var n) ? n : id)}")));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create match games from veto for match {MatchId}", matchId);
            // Non-fatal: veto completion still succeeds
        }
    }

    // ── Crypto token generation (S5) ─────────────────────────────────────────

    private static string GenerateCryptoToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static PickedMap[] ParsePicked(object? json)
    {
        if (json is null) return [];
        var str = json.ToString() ?? "[]";
        try
        {
            return JsonSerializer.Deserialize<PickedMap[]>(str, JsonDefaults.SnakeCase) ?? [];
        }
        catch { return []; }
    }

    private static string[] ParseStringArray(object? arr) => arr switch
    {
        string[] s => s,
        string   s => s.Trim('{', '}').Split(',', StringSplitOptions.RemoveEmptyEntries),
        _          => []
    };

    // ── Row mapping ──────────────────────────────────────────────────────────

    private static MatchMapVeto MapRow(dynamic row)
    {

        static string[] ParseArray(object? arr) => arr switch
        {
            string[] s => s,
            string   s => s.Trim('{', '}').Split(',', StringSplitOptions.RemoveEmptyEntries),
            _          => []
        };

        return new MatchMapVeto
        {
            Id                  = (Guid)row.id,
            MatchId             = (Guid)row.match_id,
            TournamentId        = (Guid)row.tournament_id,
            Team1Id             = (Guid?)row.team1_id,
            Team2Id             = (Guid?)row.team2_id,
            BestOf              = row.best_of ?? 1,
            Status              = row.status ?? "pending",
            CurrentTeamId       = (Guid?)row.current_team_id,
            CurrentAction       = row.current_action,
            CurrentActionNumber = row.current_action_number ?? 0,
            Team1BannedMaps     = ParseArray(row.team1_banned_maps),
            Team2BannedMaps     = ParseArray(row.team2_banned_maps),
            Team1PickedMaps     = ParsePicked(row.team1_picked_maps),
            Team2PickedMaps     = ParsePicked(row.team2_picked_maps),
            SelectedMapId       = row.selected_map_id,
            SelectedMapPool     = ParseArray(row.selected_map_pool),
            StartedAt           = row.started_at?.ToString(),
            CompletedAt         = row.completed_at?.ToString(),
            Game                = row.game ?? "valorant",
            Team1LinkToken      = (string?)row.team1_link_token,
            Team2LinkToken      = (string?)row.team2_link_token,
        };
    }
}
