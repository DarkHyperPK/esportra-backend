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

        var next = VetoEngine.NextAction(veto.BestOf, veto.CurrentActionNumber);
        await SetNextActionAsync(matchId, veto, next);

        return (await GetAsync(matchId, ct))!;
    }

    // ── Reset ────────────────────────────────────────────────────────────────

    public async Task ResetAsync(Guid matchId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
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

    // ── Crypto token generation (S5) ─────────────────────────────────────────

    private static string GenerateCryptoToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    // ── Row mapping ──────────────────────────────────────────────────────────

    private static MatchMapVeto MapRow(dynamic row)
    {
        static PickedMap[] ParsePicked(object? json)
        {
            if (json is null) return [];
            var str = json.ToString() ?? "[]";
            try { return JsonSerializer.Deserialize<PickedMap[]>(str, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []; }
            catch { return []; }
        }

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
            StartedAt           = row.started_at?.ToString(),
            CompletedAt         = row.completed_at?.ToString(),
            Game                = row.game ?? "valorant",
            Team1LinkToken      = (string?)row.team1_link_token,
            Team2LinkToken      = (string?)row.team2_link_token,
        };
    }
}
