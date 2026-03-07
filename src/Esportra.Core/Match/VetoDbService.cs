using Dapper;
using Esportra.Contracts.Database;
using System.Text.Json;

namespace Esportra.Core.Match;

/// <summary>DB-backed veto operations. All mutations are atomic updates.</summary>
public sealed class VetoDbService(IDbConnectionFactory db)
{
    // ── Fetch ────────────────────────────────────────────────────────────────

    public async Task<MatchMapVeto?> GetAsync(string matchId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleOrDefaultAsync(@"
            SELECT id, match_id, tournament_id,
                   team1_id, team2_id, best_of, status,
                   current_team_id, current_action, current_action_number,
                   team1_banned_maps, team2_banned_maps,
                   team1_picked_maps, team2_picked_maps,
                   selected_map_id, started_at, completed_at, game
            FROM public.match_map_veto
            WHERE match_id = @matchId",
            new { matchId });

        if (row is null) return null;

        return MapRow(row);
    }

    // ── Initialize veto ──────────────────────────────────────────────────────

    public async Task<MatchMapVeto> InitAsync(
        string matchId, string tournamentId,
        string? team1Id, string? team2Id,
        int bestOf, string game = "valorant",
        CancellationToken ct = default)
    {
        var firstStep = VetoSequences.GetStep(bestOf, 1)
            ?? throw new InvalidOperationException("No veto sequence for bestOf=" + bestOf);

        string? firstTeamId = firstStep.Team == "T1" ? team1Id : team2Id;

        using var conn = db.CreateConnection();

        var id = Guid.NewGuid().ToString();
        await conn.ExecuteAsync(@"
            INSERT INTO public.match_map_veto
                (id, match_id, tournament_id, team1_id, team2_id, best_of, status,
                 current_team_id, current_action, current_action_number,
                 team1_banned_maps, team2_banned_maps,
                 team1_picked_maps, team2_picked_maps,
                 started_at, game)
            VALUES
                (@id, @match_id, @tournament_id, @team1_id, @team2_id, @best_of, 'in_progress',
                 @current_team_id, @current_action, @action_number,
                 '{}', '{}',
                 '[]'::jsonb, '[]'::jsonb,
                 now(), @game)
            ON CONFLICT (match_id) DO UPDATE SET
                best_of = @best_of,
                status = 'in_progress',
                current_team_id = @current_team_id,
                current_action = @current_action,
                current_action_number = @action_number,
                started_at = now()",
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
            });

        return (await GetAsync(matchId, ct))!;
    }

    // ── Ban ──────────────────────────────────────────────────────────────────

    public async Task<MatchMapVeto> BanMapAsync(
        string matchId, string mapId, string userId, CancellationToken ct = default)
    {
        var veto = await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException("Veto not found");

        bool isTeam1 = veto.CurrentTeamId == veto.Team1Id;
        string col   = isTeam1 ? "team1_banned_maps" : "team2_banned_maps";

        var next = VetoEngine.NextAction(veto.BestOf, veto.CurrentActionNumber);
        await AdvanceOrCompleteAsync(matchId, veto, next, col, mapId, null);

        return (await GetAsync(matchId, ct))!;
    }

    // ── Pick ─────────────────────────────────────────────────────────────────

    public async Task<MatchMapVeto> PickMapAsync(
        string matchId, string mapId, string userId, CancellationToken ct = default)
    {
        var veto = await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException("Veto not found");

        bool isTeam1 = veto.CurrentTeamId == veto.Team1Id;
        string col   = isTeam1 ? "team1_picked_maps" : "team2_picked_maps";

        var next = VetoEngine.NextAction(veto.BestOf, veto.CurrentActionNumber);
        await AdvanceOrCompleteAsync(matchId, veto, next, col, mapId, null, isPick: true);

        return (await GetAsync(matchId, ct))!;
    }

    // ── Pick side ────────────────────────────────────────────────────────────

    public async Task<MatchMapVeto> PickSideAsync(
        string matchId, string mapId, string side, string userId, CancellationToken ct = default)
    {
        var veto = await GetAsync(matchId, ct)
            ?? throw new InvalidOperationException("Veto not found");

        // Update the picked map entry with the chosen side
        using var conn = db.CreateConnection();

        // Find which team picked the map and update side in their picked_maps JSONB
        await conn.ExecuteAsync(@"
            UPDATE public.match_map_veto
               SET team1_picked_maps = (
                     SELECT jsonb_agg(
                       CASE WHEN m->>'map_id' = @mapId THEN m || jsonb_build_object('side', @side) ELSE m END
                     ) FROM jsonb_array_elements(team1_picked_maps) AS m
                   ),
                   team2_picked_maps = (
                     SELECT jsonb_agg(
                       CASE WHEN m->>'map_id' = @mapId THEN m || jsonb_build_object('side', @side) ELSE m END
                     ) FROM jsonb_array_elements(team2_picked_maps) AS m
                   )
             WHERE match_id = @matchId",
            new { matchId, mapId, side });

        var next = VetoEngine.NextAction(veto.BestOf, veto.CurrentActionNumber);
        await SetNextActionAsync(matchId, veto, next);

        return (await GetAsync(matchId, ct))!;
    }

    // ── Reset ────────────────────────────────────────────────────────────────

    public async Task ResetAsync(string matchId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE public.match_map_veto
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
                   completed_at = null
             WHERE match_id = @matchId",
            new { matchId });
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    private async Task AdvanceOrCompleteAsync(
        string matchId, MatchMapVeto veto,
        (string? Action, string? TeamSide)? next,
        string arrayCol, string mapId, string? side,
        bool isPick = false)
    {
        using var conn = db.CreateConnection();

        if (isPick)
        {
            // Append to JSONB picked maps array
            await conn.ExecuteAsync($@"
                UPDATE public.match_map_veto
                   SET {arrayCol} = {arrayCol} || @entry::jsonb
                 WHERE match_id = @matchId",
                new
                {
                    matchId,
                    entry = JsonSerializer.Serialize(new { map_id = mapId, side = (string?)null }),
                });
        }
        else
        {
            // Add to text array (banned maps)
            await conn.ExecuteAsync($@"
                UPDATE public.match_map_veto
                   SET {arrayCol} = array_append({arrayCol}, @mapId)
                 WHERE match_id = @matchId",
                new { matchId, mapId });
        }

        await SetNextActionAsync(matchId, veto, next);
    }

    private async Task SetNextActionAsync(
        string matchId, MatchMapVeto veto,
        (string? Action, string? TeamSide)? next)
    {
        using var conn = db.CreateConnection();

        if (next is null)
        {
            // Veto complete
            await conn.ExecuteAsync(@"
                UPDATE public.match_map_veto
                   SET status = 'completed',
                       current_team_id = null,
                       current_action = null,
                       completed_at = now()
                 WHERE match_id = @matchId",
                new { matchId });
        }
        else
        {
            string? nextTeamId = next.Value.TeamSide == "T1" ? veto.Team1Id : veto.Team2Id;
            int nextActionNumber = veto.CurrentActionNumber + 1;

            await conn.ExecuteAsync(@"
                UPDATE public.match_map_veto
                   SET current_team_id = @nextTeamId,
                       current_action = @action,
                       current_action_number = @actionNumber,
                       status = 'in_progress'
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
            Id                  = row.id,
            MatchId             = row.match_id,
            TournamentId        = row.tournament_id,
            Team1Id             = row.team1_id,
            Team2Id             = row.team2_id,
            BestOf              = row.best_of ?? 1,
            Status              = row.status ?? "pending",
            CurrentTeamId       = row.current_team_id,
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
        };
    }
}
