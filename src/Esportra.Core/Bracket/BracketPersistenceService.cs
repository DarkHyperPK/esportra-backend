using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Core.Bracket;

/// <summary>
/// Persists a BracketGraph to Supabase and handles bracket lifecycle operations
/// (advance BYEs, reset, clear).
/// </summary>
public sealed class BracketPersistenceService(IDbConnectionFactory db)
{
    // ── Persist a generated graph ─────────────────────────────────────────────

    public async Task<BracketVersion> SaveGraphAsync(BracketGraph graph, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        using var tx   = conn.BeginTransaction();

        try
        {
        // 1. Determine version number
        var maxVersion = await conn.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(version_number), 0) FROM public.brkt_versions WHERE tournament_id = @tid",
            new { tid = graph.Version.TournamentId });
        var versionNumber = maxVersion + 1;

        // 2. Insert version
        await conn.ExecuteAsync(@"
            INSERT INTO public.brkt_versions
                (id, tournament_id, stage_id, version_number, status, created_at)
            VALUES (@id, @tournament_id, @stage_id, @version_number, @status, now())",
            new
            {
                id             = graph.Version.Id,
                tournament_id  = graph.Version.TournamentId,
                stage_id       = graph.Version.StageId,
                version_number = versionNumber,
                status         = graph.Version.Status,
            }, tx);

        // 3. Insert nodes (brkt_matches)
        foreach (var node in graph.Nodes)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO public.brkt_matches
                    (id, version_id, round_index, match_number, bracket_type, status,
                     best_of, team1_id, team2_id, group_id, round_number, scheduled_time)
                VALUES
                    (@id, @version_id, @round_index, @match_number, @bracket_type, @status,
                     @best_of, @team1_id, @team2_id, @group_id, @round_number, @scheduled_time)",
                new
                {
                    id             = node.Id,
                    version_id     = node.VersionId,
                    round_index    = node.RoundIndex,
                    match_number   = node.MatchNumber,
                    bracket_type   = node.BracketType,
                    status         = node.Status == "live" ? "in_progress" : node.Status,
                    best_of        = node.BestOf,
                    team1_id       = node.Team1Id,
                    team2_id       = node.Team2Id,
                    group_id       = node.GroupId,
                    round_number   = node.RoundNumber,
                    scheduled_time = string.IsNullOrEmpty(node.ScheduledTime)
                        ? (DateTime?)null
                        : DateTime.Parse(node.ScheduledTime, null, System.Globalization.DateTimeStyles.RoundtripKind),
                }, tx);

            // Store layout coordinates in brkt_layout
            if (node.X.HasValue || node.Y.HasValue)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO public.brkt_layout (version_id, match_id, x, y)
                    VALUES (@version_id, @match_id, @x, @y)",
                    new { version_id = node.VersionId, match_id = node.Id, x = node.X ?? 0, y = node.Y ?? 0 }, tx);
            }
        }

        // 4. Insert edges (brkt_advancements)
        foreach (var edge in graph.Edges)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO public.brkt_advancements
                    (id, version_id, source_match_id, target_match_id, type, target_slot)
                VALUES (@id, @version_id, @source_match_id, @target_match_id, @type, @target_slot)",
                new
                {
                    id             = edge.Id,
                    version_id     = edge.VersionId,
                    source_match_id = edge.SourceMatchId,
                    target_match_id = edge.TargetMatchId,
                    type           = edge.Type,
                    target_slot    = edge.TargetSlot,
                }, tx);
        }

        tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return graph.Version;
    }

    // ── Auto-advance BYE matches ──────────────────────────────────────────────

    public async Task<int> AutoAdvanceByesAsync(Guid versionId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var matches = (await conn.QueryAsync(
            "SELECT id, team1_id, team2_id, best_of FROM public.brkt_matches WHERE version_id = @versionId AND status = 'pending'",
            new { versionId })).AsList();

        int count = 0;
        foreach (var match in matches)
        {
            bool hasT1 = match.team1_id is not null;
            bool hasT2 = match.team2_id is not null;

            if (!(hasT1 ^ hasT2)) continue; // both or neither → skip

            Guid winnerId = hasT1 ? (Guid)match.team1_id : (Guid)match.team2_id;
            int    bestOf   = (int)(match.best_of ?? 1);
            int    winScore = bestOf == 1 ? 13 : (int)Math.Ceiling(bestOf / 2.0);
            int    t1Score  = hasT1 ? winScore : 0;
            int    t2Score  = hasT2 ? winScore : 0;

            await conn.ExecuteAsync(@"
                UPDATE public.brkt_matches
                   SET status = 'completed', winner_id = @winnerId, loser_id = null,
                       team1_score = @t1, team2_score = @t2
                 WHERE id = @id",
                new { winnerId, t1 = t1Score, t2 = t2Score, id = (Guid)match.id });

            // Advance winner along edges
            await AdvanceTeamAsync(conn, (Guid)match.id, versionId, "winner", winnerId);
            count++;
        }

        return count;
    }

    // ── Reset (keep structure, clear results) ────────────────────────────────

    public async Task ResetAsync(Guid versionId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var matches = (await conn.QueryAsync(
            "SELECT id, round_index FROM public.brkt_matches WHERE version_id = @versionId",
            new { versionId })).AsList();

        foreach (var match in matches)
        {
            if ((int)match.round_index == 0)
            {
                await conn.ExecuteAsync(
                    "UPDATE public.brkt_matches SET status='pending', winner_id=null, loser_id=null WHERE id=@id",
                    new { id = (Guid)match.id });
            }
            else
            {
                await conn.ExecuteAsync(
                    "UPDATE public.brkt_matches SET status='pending', team1_id=null, team2_id=null, winner_id=null, loser_id=null WHERE id=@id",
                    new { id = (Guid)match.id });
            }
        }

        var matchIds = matches.Select(m => (Guid)m.id).ToArray();
        await conn.ExecuteAsync(
            "DELETE FROM public.brkt_match_events WHERE match_id = ANY(@ids)",
            new { ids = matchIds });
    }

    // ── Clear (delete entire version) ────────────────────────────────────────

    public async Task ClearAsync(Guid versionId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        // All child tables (brkt_matches, brkt_layout, brkt_advancements, brkt_match_events,
        // brkt_match_games, match_checkins, etc.) cascade from brkt_versions.
        await conn.ExecuteAsync("DELETE FROM public.brkt_versions WHERE id = @v", new { v = versionId });
    }

    // ── Internal: advance a team along edges ─────────────────────────────────

    private static async Task AdvanceTeamAsync(System.Data.IDbConnection conn,
        Guid sourceMatchId, Guid versionId, string edgeType, Guid teamId)
    {
        var edges = (await conn.QueryAsync(
            "SELECT target_match_id, target_slot FROM public.brkt_advancements WHERE version_id=@v AND source_match_id=@s AND type=@t",
            new { v = versionId, s = sourceMatchId, t = edgeType })).AsList();

        foreach (var edge in edges)
        {
            string col = (int)edge.target_slot == 1 ? "team1_id" : "team2_id";
            await conn.ExecuteAsync(
                $"UPDATE public.brkt_matches SET {col} = @teamId WHERE id = @targetId",
                new { teamId, targetId = (Guid)edge.target_match_id });
        }
    }

    /// <summary>
    /// Replaces <c>notify_match_ready()</c> DB trigger.
    /// Notifies captains of both teams when a match has both teams assigned.
    /// Called on bracket publish and after BYE auto-advancement.
    /// </summary>
    public static async Task NotifyMatchReadyCaptainsAsync(
        System.Data.IDbConnection conn, Guid matchId, Guid team1Id, Guid team2Id)
    {
        var captainIds = (await conn.QueryAsync<Guid>(
            """
            SELECT tm.user_id FROM public.team_members tm
            WHERE tm.team_id IN (@t1, @t2) AND tm.role = 'captain' AND tm.is_active = true
            """,
            new { t1 = team1Id, t2 = team2Id })).AsList();

        foreach (var captainId in captainIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@userId, 'match_ready', 'Match Ready',
                        'Your match is ready. Head to the Captain dashboard to start the map veto.',
                        '/tournaments/captain',
                        jsonb_build_object('match_id', @matchId::text)::jsonb, false)
                """,
                new { userId = captainId, matchId });
        }
    }
}
