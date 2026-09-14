using Dapper;
using Esportra.Contracts.Database;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.Json;

namespace Esportra.Core.Match;

/// <summary>DB-backed veto operations with FSM validation and optimistic locking.</summary>
public sealed class VetoDbService(IDbConnectionFactory db, IVetoSettingsRepository settingsRepo, ILogger<VetoDbService> logger)
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
            ) OR EXISTS(
                SELECT 1 FROM tournament_participants
                WHERE id = @teamId
                  AND participant_type = 'solo'
                  AND user_id = @userId
            )",
            new { teamId, userId });
        return isCaptain;
    }

    /// <summary>Check if user is the tournament organizer or org staff admin.</summary>
    public async Task<bool> IsOrganizerAsync(Guid userId, Guid tournamentId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        // Keep in sync with StaffAuthHelper.StaffOrgTournamentLinkSql (Esportra.Api).
        return await conn.QuerySingleOrDefaultAsync<bool>(@"
            SELECT EXISTS(
                SELECT 1 FROM tournaments t
                WHERE t.id = @tournamentId AND t.organizer_id = @userId
            ) OR EXISTS(
                SELECT 1 FROM organization_staff os
                JOIN tournaments t ON t.id = @tournamentId
                WHERE os.user_id = @userId
                  AND os.status = 'active'
                  AND (
                      (t.organization_id IS NOT NULL AND os.organization_id = t.organization_id)
                      OR (
                          os.role = 'admin'
                          AND EXISTS (
                              SELECT 1 FROM organizations o
                              WHERE o.id = os.organization_id
                                AND o.owner_id = t.organizer_id
                          )
                      )
                  )
                  AND os.role IN ('owner','admin')
            )",
            new { tournamentId, userId });
    }

    // ── Veto action history ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<VetoActionHistory>> GetHistoryAsync(
        Guid matchId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync(@"
            SELECT id, veto_id, match_id, tournament_id, team_id, team_side,
                   action_type, map_id::text AS map_id, action_number, side,
                   created_by, created_at::text AS created_at
            FROM public.match_map_veto_actions
            WHERE match_id = @matchId
            ORDER BY action_number ASC",
            new { matchId });

        return rows.Select(r => new VetoActionHistory(
            (Guid)r.id,
            (Guid)r.veto_id,
            (Guid)r.match_id,
            (Guid?)r.tournament_id,
            (Guid?)r.team_id,
            (string?)r.team_side,
            (string)r.action_type,
            (string?)r.map_id,
            (int)r.action_number,
            (string?)r.side,
            (Guid?)r.created_by,
            (string?)r.created_at)).ToList();
    }

    /// <summary>Enriched veto history with team and map metadata for UI display.</summary>
    public async Task<IReadOnlyList<VetoHistoryEntry>> GetEnrichedHistoryAsync(
        Guid matchId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync(@"
            SELECT a.action_number,
                   COALESCE(a.team_side, 'team1') AS team_side,
                   a.team_id,
                   COALESCE(t.name, tpart.team_name, solop.username) AS team_name,
                   a.action_type AS action,
                   a.map_id::text AS map_id,
                   gm.map_name,
                   gm.map_image_url,
                   a.side,
                   a.created_at::text AS created_at
            FROM public.match_map_veto_actions a
            LEFT JOIN public.teams t ON t.id = a.team_id
            LEFT JOIN public.tournament_participants tpart ON tpart.id = a.team_id
              AND (tpart.is_mock = TRUE OR tpart.participant_type = 'solo')
            LEFT JOIN public.profiles solop ON solop.id = tpart.user_id
            LEFT JOIN public.game_maps gm ON gm.id = a.map_id
            WHERE a.match_id = @matchId
            ORDER BY a.action_number ASC",
            new { matchId });

        return rows.Select(r => new VetoHistoryEntry(
            (int)r.action_number,
            (string)r.team_side,
            (Guid?)r.team_id,
            (string?)r.team_name,
            (string)r.action,
            (string)r.map_id,
            (string?)r.map_name,
            (string?)r.map_image_url,
            (string?)r.side,
            (string?)r.created_at)).ToList();
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

        using var conn = db.CreateConnection();

        var mapPoolIds = (await conn.QueryAsync<string>(@"
            SELECT tmp.map_id::text
            FROM public.tournament_map_pools tmp
            JOIN public.game_maps gm ON gm.id = tmp.map_id
            WHERE tmp.tournament_id = @tournamentId
            ORDER BY gm.map_name ASC",
            new { tournamentId })).ToArray();

        var gameConfig = VetoSequences.GetGameConfig(game);
        if (mapPoolIds.Length != gameConfig.MapPoolSize)
        {
            throw new InvalidOperationException(
                $"INVALID_POOL_SIZE: expected {gameConfig.MapPoolSize} maps for {game}, got {mapPoolIds.Length}");
        }

        var poolSize = mapPoolIds.Length;
        var initSettings = await settingsRepo.GetAsync(matchId, ct);
        var initResolver = VetoSequenceResolverFactory.Create(initSettings);
        var stub = new MatchMapVeto { BestOf = bestOf, Game = game, SelectedMapPool = mapPoolIds };
        var firstStep = initResolver.GetStep(stub, 1, poolSize)
            ?? throw new InvalidOperationException("No veto sequence for bestOf=" + bestOf);
        Guid? firstTeamId = firstStep.Action == "ignore"
            ? null
            : (firstStep.Team == "T1" ? team1Id : team2Id);

        // Generate cryptographic tokens for team links (S5)
        var team1Token = GenerateCryptoToken();
        var team2Token = GenerateCryptoToken();

        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO public.match_map_vetos
                (id, match_id, tournament_id, team1_id, team2_id, best_of, status,
                 current_team_id, current_action, current_action_number,
                 team1_banned_maps, team2_banned_maps,
                 team1_picked_maps, team2_picked_maps,
                 selected_map_pool, started_at, game, team1_link_token, team2_link_token,
                 turn_started_at)
            VALUES
                (@id, @match_id, @tournament_id, @team1_id, @team2_id, @best_of, 'in_progress',
                 @current_team_id, @current_action, @action_number,
                 '{}', '{}',
                 '[]'::json, '[]'::json,
                 @selected_map_pool, now(), @game, @team1_token, @team2_token, now())
            ON CONFLICT (match_id) DO UPDATE SET
                team1_id = @team1_id,
                team2_id = @team2_id,
                best_of = @best_of,
                status = 'in_progress',
                current_team_id = @current_team_id,
                current_action = @current_action,
                current_action_number = @action_number,
                team1_banned_maps = '{}',
                team2_banned_maps = '{}',
                team1_picked_maps = '[]'::json,
                team2_picked_maps = '[]'::json,
                selected_map_id = null,
                selected_map_pool = @selected_map_pool,
                completed_at = null,
                started_at = now(),
                turn_started_at = now(),
                game = @game,
                team1_link_token = CASE
                    WHEN match_map_vetos.team1_id IS DISTINCT FROM @team1_id THEN @team1_token
                    ELSE COALESCE(match_map_vetos.team1_link_token, @team1_token)
                END,
                team2_link_token = CASE
                    WHEN match_map_vetos.team2_id IS DISTINCT FROM @team2_id THEN @team2_token
                    ELSE COALESCE(match_map_vetos.team2_link_token, @team2_token)
                END",
            new
            {
                id,
                match_id = matchId,
                tournament_id = tournamentId,
                team1_id = team1Id,
                team2_id = team2Id,
                best_of = bestOf,
                current_team_id = firstTeamId,
                current_action = firstStep.Action,
                action_number = firstStep.ActionNumber,
                selected_map_pool = mapPoolIds,
                game,
                team1_token = team1Token,
                team2_token = team2Token,
            });

        await conn.ExecuteAsync(
            "DELETE FROM public.match_map_veto_actions WHERE match_id = @matchId",
            new { matchId });

        return await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException($"Veto for match {matchId} not found after write.");
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
        string col = isTeam1 ? "team1_banned_maps" : "team2_banned_maps";

        var settings = await settingsRepo.GetAsync(matchId, ct);
        var resolver = VetoSequenceResolverFactory.Create(settings);
        var next = NextActionFor(veto, resolver);

        // D4: Optimistic lock — only update if action_number hasn't changed
        await AdvanceOrCompleteAsync(matchId, veto, next, col, mapId, null, userId, "ban", resolver, ct: ct);

        return await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException($"Veto for match {matchId} not found after write.");
    }

    public async Task<MatchMapVeto> BanMapForTeamTokenAsync(
        Guid matchId, string mapId, string teamSide, CancellationToken ct = default)
    {
        var veto = await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException("Veto not found");

        ValidateAction(veto, VetoEvent.BanMap, mapId);
        AssertTokenTeamCanAct(veto, teamSide);

        bool isTeam1 = veto.CurrentTeamId == veto.Team1Id;
        string col = isTeam1 ? "team1_banned_maps" : "team2_banned_maps";

        var settings = await settingsRepo.GetAsync(matchId, ct);
        var resolver = VetoSequenceResolverFactory.Create(settings);
        var next = NextActionFor(veto, resolver);

        await AdvanceOrCompleteAsync(matchId, veto, next, col, mapId, null, null, "ban", resolver, ct: ct);

        return await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException($"Veto for match {matchId} not found after write.");
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
        string col = isTeam1 ? "team1_picked_maps" : "team2_picked_maps";

        var settings = await settingsRepo.GetAsync(matchId, ct);
        var resolver = VetoSequenceResolverFactory.Create(settings);
        var next = NextActionFor(veto, resolver);
        await AdvanceOrCompleteAsync(matchId, veto, next, col, mapId, null, userId, "pick", resolver, isPick: true, ct: ct);

        return await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException($"Veto for match {matchId} not found after write.");
    }

    public async Task<MatchMapVeto> PickMapForTeamTokenAsync(
        Guid matchId, string mapId, string teamSide, CancellationToken ct = default)
    {
        var veto = await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException("Veto not found");

        ValidateAction(veto, VetoEvent.PickMap, mapId);
        AssertTokenTeamCanAct(veto, teamSide);

        bool isTeam1 = veto.CurrentTeamId == veto.Team1Id;
        string col = isTeam1 ? "team1_picked_maps" : "team2_picked_maps";

        var settings = await settingsRepo.GetAsync(matchId, ct);
        var resolver = VetoSequenceResolverFactory.Create(settings);
        var next = NextActionFor(veto, resolver);
        await AdvanceOrCompleteAsync(matchId, veto, next, col, mapId, null, null, "pick", resolver, isPick: true, ct: ct);

        return await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException($"Veto for match {matchId} not found after write.");
    }

    // ── Pick side (with FSM + optimistic lock) ───────────────────────────────

    public async Task<MatchMapVeto> PickSideAsync(
        Guid matchId, string mapId, string side, Guid userId, CancellationToken ct = default)
    {
        var veto = await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException("Veto not found");

        ValidateAction(veto, VetoEvent.PickSide, mapId);
        await AssertIsCaptainOfCurrentTeam(userId, veto, ct);

        var pickSideSettings = await settingsRepo.GetAsync(matchId, ct);
        var pickSideResolver = VetoSequenceResolverFactory.Create(pickSideSettings);

        using var conn = db.CreateConnection();

        // B3: COALESCE to prevent jsonb_agg NULL when array is empty
        // Update side on existing picked entries
        var updated = await conn.ExecuteAsync(@"
            UPDATE public.match_map_vetos
               SET team1_picked_maps = (
                     COALESCE((
                       SELECT jsonb_agg(
                         CASE WHEN m->>'map_id' = @mapId THEN m || jsonb_build_object('side', @side) ELSE m END
                       ) FROM jsonb_array_elements((COALESCE(team1_picked_maps::text, '[]'))::jsonb) AS m
                     ), '[]'::jsonb)
                   )::json,
                   team2_picked_maps = (
                     COALESCE((
                       SELECT jsonb_agg(
                         CASE WHEN m->>'map_id' = @mapId THEN m || jsonb_build_object('side', @side) ELSE m END
                       ) FROM jsonb_array_elements((COALESCE(team2_picked_maps::text, '[]'))::jsonb) AS m
                     ), '[]'::jsonb)
                   )::json
             WHERE match_id = @matchId
               AND current_action_number = @expectedAction",
            new { matchId, mapId, side, expectedAction = veto.CurrentActionNumber });

        if (updated == 0)
            throw new InvalidOperationException("CONFLICT: veto state changed (optimistic lock)");

        // Decider fix: if the map wasn't in either team's picks (leftover map),
        // append it to the current team's picks so the side is stored.
        // Only do this for decider maps — not for regular side picks where
        // the map belongs to the opposing team.
        var step = CurrentStepFor(veto, pickSideResolver);
        if (step?.IsDecider == true)
        {
            var isTeam1 = veto.CurrentTeamId == veto.Team1Id;
            var appendSql = isTeam1
                ? @"UPDATE public.match_map_vetos
                       SET team1_picked_maps = (
                             (COALESCE(team1_picked_maps::text, '[]'))::jsonb
                             || jsonb_build_array(jsonb_build_object('map_id', @mapId::text, 'side', @side))
                           )::json,
                           selected_map_id = @mapId::uuid
                     WHERE match_id = @matchId
                       AND NOT EXISTS (
                           SELECT 1 FROM jsonb_array_elements((COALESCE(team1_picked_maps::text, '[]'))::jsonb) m
                            WHERE m->>'map_id' = @mapId
                       )"
                : @"UPDATE public.match_map_vetos
                       SET team2_picked_maps = (
                             (COALESCE(team2_picked_maps::text, '[]'))::jsonb
                             || jsonb_build_array(jsonb_build_object('map_id', @mapId::text, 'side', @side))
                           )::json,
                           selected_map_id = @mapId::uuid
                     WHERE match_id = @matchId
                       AND NOT EXISTS (
                           SELECT 1 FROM jsonb_array_elements((COALESCE(team2_picked_maps::text, '[]'))::jsonb) m
                            WHERE m->>'map_id' = @mapId
                       )";
            await conn.ExecuteAsync(appendSql, new { matchId, mapId, side });
        }

        await RecordHistoryAsync(conn, veto, mapId, userId, "pick_side", side);

        var pickSideNext = NextActionFor(veto, pickSideResolver);
        await SetNextActionAsync(matchId, veto, pickSideNext, pickSideResolver, ct);

        return await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException($"Veto for match {matchId} not found after write.");
    }

    public async Task<MatchMapVeto> PickSideForTeamTokenAsync(
        Guid matchId, string mapId, string side, string teamSide, CancellationToken ct = default)
    {
        var veto = await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException("Veto not found");

        ValidateAction(veto, VetoEvent.PickSide, mapId);
        AssertTokenTeamCanAct(veto, teamSide);

        var tokenSideSettings = await settingsRepo.GetAsync(matchId, ct);
        var tokenSideResolver = VetoSequenceResolverFactory.Create(tokenSideSettings);

        using var conn = db.CreateConnection();

        var updated = await conn.ExecuteAsync(@"
            UPDATE public.match_map_vetos
               SET team1_picked_maps = (
                     COALESCE((
                       SELECT jsonb_agg(
                         CASE WHEN m->>'map_id' = @mapId THEN m || jsonb_build_object('side', @side) ELSE m END
                       ) FROM jsonb_array_elements((COALESCE(team1_picked_maps::text, '[]'))::jsonb) AS m
                     ), '[]'::jsonb)
                   )::json,
                   team2_picked_maps = (
                     COALESCE((
                       SELECT jsonb_agg(
                         CASE WHEN m->>'map_id' = @mapId THEN m || jsonb_build_object('side', @side) ELSE m END
                       ) FROM jsonb_array_elements((COALESCE(team2_picked_maps::text, '[]'))::jsonb) AS m
                     ), '[]'::jsonb)
                   )::json
             WHERE match_id = @matchId
               AND current_action_number = @expectedAction",
            new { matchId, mapId, side, expectedAction = veto.CurrentActionNumber });

        if (updated == 0)
            throw new InvalidOperationException("CONFLICT: veto state changed (optimistic lock)");

        var tokenStep = CurrentStepFor(veto, tokenSideResolver);
        if (tokenStep?.IsDecider == true)
        {
            var isTeam1 = veto.CurrentTeamId == veto.Team1Id;
            var appendSql = isTeam1
                ? @"UPDATE public.match_map_vetos
                       SET team1_picked_maps = (
                             (COALESCE(team1_picked_maps::text, '[]'))::jsonb
                             || jsonb_build_array(jsonb_build_object('map_id', @mapId::text, 'side', @side))
                           )::json,
                           selected_map_id = @mapId::uuid
                     WHERE match_id = @matchId
                       AND NOT EXISTS (
                           SELECT 1 FROM jsonb_array_elements((COALESCE(team1_picked_maps::text, '[]'))::jsonb) m
                            WHERE m->>'map_id' = @mapId
                       )"
                : @"UPDATE public.match_map_vetos
                       SET team2_picked_maps = (
                             (COALESCE(team2_picked_maps::text, '[]'))::jsonb
                             || jsonb_build_array(jsonb_build_object('map_id', @mapId::text, 'side', @side))
                           )::json,
                           selected_map_id = @mapId::uuid
                     WHERE match_id = @matchId
                       AND NOT EXISTS (
                           SELECT 1 FROM jsonb_array_elements((COALESCE(team2_picked_maps::text, '[]'))::jsonb) m
                            WHERE m->>'map_id' = @mapId
                       )";
            await conn.ExecuteAsync(appendSql, new { matchId, mapId, side });
        }

        await RecordHistoryAsync(conn, veto, mapId, null, "pick_side", side);

        var tokenSideNext = NextActionFor(veto, tokenSideResolver);
        await SetNextActionAsync(matchId, veto, tokenSideNext, tokenSideResolver, ct);

        return await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException($"Veto for match {matchId} not found after write.");
    }

    // ── Reset ────────────────────────────────────────────────────────────────

    public async Task ResetAsync(Guid matchId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        // Delete game rows created from the previous veto so stale data doesn't survive
        await conn.ExecuteAsync(
            "DELETE FROM public.brkt_match_games WHERE match_id = @matchId",
            new { matchId });

        await conn.ExecuteAsync(
            "DELETE FROM public.match_map_veto_actions WHERE match_id = @matchId",
            new { matchId });

        await conn.ExecuteAsync(@"
            UPDATE public.match_map_vetos
               SET status = 'pending',
                   current_team_id = null,
                   current_action = null,
                   current_action_number = 0,
                   team1_banned_maps = '{}',
                   team2_banned_maps = '{}',
                   team1_picked_maps = '[]'::json,
                   team2_picked_maps = '[]'::json,
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
        ValidateEventMatchesState(ev, state);
        ValidateMapNotReused(mapId, ev, veto);
    }

    private static void ValidateEventMatchesState(VetoEvent ev, VetoState state)
    {
        if (ev == VetoEvent.BanMap && state != VetoState.Ban)
            throw new InvalidOperationException($"INVALID_STATE: expected Ban, got {state}");
        if (ev == VetoEvent.PickMap && state != VetoState.Pick)
            throw new InvalidOperationException($"INVALID_STATE: expected Pick, got {state}");
        if (ev == VetoEvent.PickSide && state != VetoState.PickSide)
            throw new InvalidOperationException($"INVALID_STATE: expected PickSide, got {state}");
        if (state == VetoState.Complete)
            throw new InvalidOperationException("INVALID_STATE: veto already completed");
    }

    private static void ValidateMapNotReused(string? mapId, VetoEvent ev, MatchMapVeto veto)
    {
        if (mapId is null)
            return;
        bool isBanned = veto.Team1BannedMaps.Contains(mapId) || veto.Team2BannedMaps.Contains(mapId);
        bool isPicked = veto.Team1PickedMaps.Any(p => p.MapId == mapId)
                     || veto.Team2PickedMaps.Any(p => p.MapId == mapId);
        if (isBanned)
            throw new InvalidOperationException("MAP_ALREADY_USED: map is already banned");
        if (ev != VetoEvent.PickSide && isPicked)
            throw new InvalidOperationException("MAP_ALREADY_USED: map is already picked");
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

    private static void AssertTokenTeamCanAct(MatchMapVeto veto, string teamSide)
    {
        if (veto.CurrentTeamId is null)
            throw new InvalidOperationException("No current team assigned");

        var expectedTeamId = teamSide == "team1" ? veto.Team1Id : teamSide == "team2" ? veto.Team2Id : null;
        if (expectedTeamId is null || veto.CurrentTeamId != expectedTeamId)
            throw new UnauthorizedAccessException("NOT_YOUR_TURN: token is not for the acting team");
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    private async Task AdvanceOrCompleteAsync(
        Guid matchId, MatchMapVeto veto,
        VetoStep? next,
        string arrayCol, string mapId, string? side,
        Guid? userId, string actionType,
        IVetoSequenceResolver resolver,
        bool isPick = false,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        // D4: Optimistic lock — only update if current_action_number matches
        int updated;
        if (isPick)
        {
            updated = await conn.ExecuteAsync($@"
                UPDATE public.match_map_vetos
                   SET {arrayCol} = (
                         (COALESCE({arrayCol}::text, '[]'))::jsonb
                         || jsonb_build_array(jsonb_build_object('map_id', @mapId::text, 'side', null::text))
                       )::json
                 WHERE match_id = @matchId
                   AND current_action_number = @expectedAction",
                new
                {
                    matchId,
                    mapId,
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

        await RecordHistoryAsync(conn, veto, mapId, userId, actionType, side);
        await SetNextActionAsync(matchId, veto, next, resolver, ct);
    }

    private static int PoolSizeFor(MatchMapVeto veto)
    {
        if (veto.SelectedMapPool.Length > 0)
            return veto.SelectedMapPool.Length;
        return VetoSequences.GetGameConfig(veto.Game ?? "valorant").MapPoolSize;
    }

    private static VetoStep? NextActionFor(MatchMapVeto veto, IVetoSequenceResolver resolver)
    {
        var poolSize = PoolSizeFor(veto);
        return resolver.GetStep(veto, veto.CurrentActionNumber + 1, poolSize);
    }

    private static VetoStep? CurrentStepFor(MatchMapVeto veto, IVetoSequenceResolver resolver)
    {
        var poolSize = PoolSizeFor(veto);
        return resolver.GetStep(veto, veto.CurrentActionNumber, poolSize);
    }

    private static async Task RecordHistoryAsync(
        System.Data.IDbConnection conn,
        MatchMapVeto veto,
        string mapId,
        Guid? userId,
        string actionType,
        string? side)
    {
        var isTeam1 = veto.CurrentTeamId == veto.Team1Id;
        var teamSide = isTeam1 ? "team1" : "team2";
        var teamId = isTeam1 ? veto.Team1Id : veto.Team2Id;

        await conn.ExecuteAsync(@"
            INSERT INTO public.match_map_veto_actions
                (veto_id, match_id, tournament_id, team_id, team_side,
                 action_type, map_id, action_number, side, created_by, created_at)
            VALUES
                (@vetoId, @matchId, @tournamentId, @teamId, @teamSide,
                 @actionType, @mapId::uuid, @actionNumber, @side, @createdBy, now())
            ON CONFLICT (veto_id, action_number) DO NOTHING",
            new
            {
                vetoId = veto.Id,
                matchId = veto.MatchId,
                tournamentId = veto.TournamentId,
                teamId,
                teamSide,
                actionType,
                mapId,
                actionNumber = veto.CurrentActionNumber,
                side,
                createdBy = userId,
            });
    }

    private async Task SetNextActionAsync(
        Guid matchId, MatchMapVeto veto,
        VetoStep? next,
        IVetoSequenceResolver resolver,
        CancellationToken ct)
    {
        if (next is null)
        {
            await CompleteVetoAsync(matchId, veto);
            return;
        }

        await WriteNextStepAsync(matchId, veto, next);

        var currentStep = next;
        while (currentStep.Action == "ignore")
        {
            var current = await GetAsync(matchId, ct)
                ?? throw new InvalidOperationException($"Veto for match {matchId} not found during ignore step.");
            await ExecuteIgnoreBanAsync(matchId, current);
            var refetched = await GetAsync(matchId, ct)
                ?? throw new InvalidOperationException($"Veto for match {matchId} not found after ignore write.");
            var nextStep = NextActionFor(refetched, resolver);
            if (nextStep is null)
            {
                await CompleteVetoAsync(matchId, refetched);
                return;
            }
            await WriteNextStepAsync(matchId, refetched, nextStep);
            currentStep = nextStep;
        }
    }

    private async Task CompleteVetoAsync(Guid matchId, MatchMapVeto veto)
    {
        using var conn = db.CreateConnection();
        // D5: Auto-set selected_map_id for BO1 (last remaining unpicked/unbanned map)
        string? selectedMapSql = veto.BestOf == 1
            ? ", selected_map_id = (SELECT unnest(selected_map_pool) EXCEPT SELECT unnest(team1_banned_maps) EXCEPT SELECT unnest(team2_banned_maps) LIMIT 1)::uuid"
            : null;
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

    private async Task WriteNextStepAsync(Guid matchId, MatchMapVeto veto, VetoStep next)
    {
        Guid? nextTeamId = next.Action == "ignore"
            ? null
            : (next.Team == "T1" ? veto.Team1Id : veto.Team2Id);
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE public.match_map_vetos
               SET current_team_id = @nextTeamId,
                   current_action = @action,
                   current_action_number = @actionNumber,
                   status = 'in_progress',
                   turn_started_at = now()
             WHERE match_id = @matchId",
            new { matchId, nextTeamId, action = next.Action, actionNumber = veto.CurrentActionNumber + 1 });
    }

    private async Task ExecuteIgnoreBanAsync(Guid matchId, MatchMapVeto veto)
    {
        var pool = veto.SelectedMapPool.ToHashSet();
        var excluded = new HashSet<string>(veto.Team1BannedMaps.Concat(veto.Team2BannedMaps));
        excluded.UnionWith(veto.Team1PickedMaps.Select(p => p.MapId));
        excluded.UnionWith(veto.Team2PickedMaps.Select(p => p.MapId));
        var remaining = pool.Except(excluded).ToArray();
        if (remaining.Length == 0)
            throw new InvalidOperationException("INVALID_SEQUENCE: ignore step has no remaining maps in pool.");
        var ignored = remaining[Random.Shared.Next(remaining.Length)];
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE public.match_map_vetos
               SET team1_banned_maps = array_append(team1_banned_maps, @mapId),
                   team2_banned_maps = array_append(team2_banned_maps, @mapId)
             WHERE match_id = @matchId",
            new { matchId, mapId = ignored });
        await conn.ExecuteAsync(@"
            INSERT INTO public.match_map_veto_actions
                (veto_id, match_id, tournament_id, team_id, team_side,
                 action_type, map_id, action_number, side, created_by, created_at)
            VALUES
                (@vetoId, @matchId, @tournamentId, NULL, NULL,
                 'ignore', @mapId::uuid, @actionNumber, NULL, NULL, now())
            ON CONFLICT (veto_id, action_number) DO NOTHING",
            new { vetoId = veto.Id, matchId, tournamentId = veto.TournamentId, mapId = ignored, actionNumber = veto.CurrentActionNumber });
    }

    /// <summary>
    /// Derives the ordered list of (gameNumber, mapId, mapName) for a completed veto.
    /// Used by scan endpoint and accept endpoint to resolve which map each game uses.
    /// </summary>
    public async Task<List<(int GameNumber, string MapId, string MapName)>> GetGameMapOrderAsync(
        Guid matchId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var veto = await conn.QuerySingleOrDefaultAsync<dynamic>(@"
            SELECT team1_picked_maps, team2_picked_maps, selected_map_id::text as selected_map_id,
                   team1_banned_maps, team2_banned_maps, selected_map_pool, best_of
            FROM public.match_map_vetos
            WHERE match_id = @matchId AND status = 'completed'", new { matchId });

        if (veto is null) return [];

        int bestOf = Convert.ToInt32(veto.best_of ?? 1);
        PickedMap[] t1Picked = ParsePicked(veto.team1_picked_maps);
        PickedMap[] t2Picked = ParsePicked(veto.team2_picked_maps);
        string? selectedMapId = (string?)veto.selected_map_id;

        // Compute decider if not stored
        if (selectedMapId is null && bestOf > 1)
        {
            string[] pool = ParseStringArray(veto.selected_map_pool);
            string[] bans1 = ParseStringArray(veto.team1_banned_maps);
            string[] bans2 = ParseStringArray(veto.team2_banned_maps);
            var picks = new HashSet<string>(
                t1Picked.Select(p => p.MapId).Concat(t2Picked.Select(p => p.MapId)));
            var allExcluded = new HashSet<string>(bans1.Concat(bans2));
            allExcluded.UnionWith(picks);
            selectedMapId = pool.FirstOrDefault(m => !allExcluded.Contains(m));
        }

        var gameMapIds = await ResolvePickedMapOrderFromHistoryAsync(conn, matchId, selectedMapId);

        if (gameMapIds.Count == 0) return [];

        var mapNames = (await conn.QueryAsync<dynamic>(@"
            SELECT id::text as id, map_name
            FROM public.game_maps
            WHERE id::text = ANY(@ids)",
            new { ids = gameMapIds.ToArray() })).ToDictionary(
                m => (string)m.id,
                m => (string)m.map_name);

        var result = new List<(int, string, string)>();
        for (int i = 0; i < gameMapIds.Count; i++)
        {
            var mapId = gameMapIds[i];
            mapNames.TryGetValue(mapId, out var mapName);
            result.Add((i + 1, mapId, mapName ?? "Unknown"));
        }
        return result;
    }

    private static async Task<List<string>> ResolvePickedMapOrderFromHistoryAsync(
        System.Data.IDbConnection conn, Guid matchId, string? selectedMapId)
    {
        var picks = (await conn.QueryAsync<string>(@"
            SELECT map_id::text
            FROM public.match_map_veto_actions
            WHERE match_id = @matchId
              AND action_type = 'pick'
            ORDER BY action_number ASC",
            new { matchId })).ToList();
        if (selectedMapId is not null) picks.Add(selectedMapId);
        return picks;
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
        string s => s.Trim('{', '}').Split(',', StringSplitOptions.RemoveEmptyEntries),
        _ => []
    };

    // ── Row mapping ──────────────────────────────────────────────────────────

    private static MatchMapVeto MapRow(dynamic row)
    {

        static string[] ParseArray(object? arr) => arr switch
        {
            string[] s => s,
            string s => s.Trim('{', '}').Split(',', StringSplitOptions.RemoveEmptyEntries),
            _ => []
        };

        return new MatchMapVeto
        {
            Id = (Guid)row.id,
            MatchId = (Guid)row.match_id,
            TournamentId = (Guid)row.tournament_id,
            Team1Id = (Guid?)row.team1_id,
            Team2Id = (Guid?)row.team2_id,
            BestOf = row.best_of ?? 1,
            Status = row.status ?? "pending",
            CurrentTeamId = (Guid?)row.current_team_id,
            CurrentAction = row.current_action,
            CurrentActionNumber = row.current_action_number ?? 0,
            Team1BannedMaps = ParseArray(row.team1_banned_maps),
            Team2BannedMaps = ParseArray(row.team2_banned_maps),
            Team1PickedMaps = ParsePicked(row.team1_picked_maps),
            Team2PickedMaps = ParsePicked(row.team2_picked_maps),
            SelectedMapId = row.selected_map_id,
            SelectedMapPool = ParseArray(row.selected_map_pool),
            StartedAt = row.started_at?.ToString(),
            CompletedAt = row.completed_at?.ToString(),
            Game = row.game ?? "valorant",
            Team1LinkToken = (string?)row.team1_link_token,
            Team2LinkToken = (string?)row.team2_link_token,
        };
    }
}
