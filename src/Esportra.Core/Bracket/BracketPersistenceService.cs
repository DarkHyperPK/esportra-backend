using Dapper;
using Esportra.Contracts.Database;
using Esportra.Core.Notifications;

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
        using var tx = conn.BeginTransaction();

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
                    id = graph.Version.Id,
                    tournament_id = graph.Version.TournamentId,
                    stage_id = graph.Version.StageId,
                    version_number = versionNumber,
                    status = graph.Version.Status,
                }, tx);

            // 3. Insert nodes (brkt_matches)
            foreach (var node in graph.Nodes)
            {
                await conn.ExecuteAsync(@"
                INSERT INTO public.brkt_matches
                    (id, version_id, round_index, match_number, bracket_type, status,
                     best_of, team1_id, team2_id, group_id, round_number, scheduled_time,
                     team1_seed, team2_seed)
                VALUES
                    (@id, @version_id, @round_index, @match_number, @bracket_type, @status,
                     @best_of, @team1_id, @team2_id, @group_id, @round_number, @scheduled_time,
                     @team1_seed, @team2_seed)",
                    new
                    {
                        id = node.Id,
                        version_id = node.VersionId,
                        round_index = node.RoundIndex,
                        match_number = node.MatchNumber,
                        bracket_type = node.BracketType,
                        status = node.Status == "live" ? "in_progress" : node.Status,
                        best_of = node.BestOf,
                        team1_id = node.Team1Id,
                        team2_id = node.Team2Id,
                        group_id = node.GroupId,
                        round_number = node.RoundNumber,
                        scheduled_time = string.IsNullOrEmpty(node.ScheduledTime)
                            ? (DateTime?)null
                            : DateTime.Parse(node.ScheduledTime, null, System.Globalization.DateTimeStyles.RoundtripKind),
                        team1_seed = node.Team1Seed,
                        team2_seed = node.Team2Seed,
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
                        id = edge.Id,
                        version_id = edge.VersionId,
                        source_match_id = edge.SourceMatchId,
                        target_match_id = edge.TargetMatchId,
                        type = edge.Type,
                        target_slot = edge.TargetSlot,
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
            "SELECT id, team1_id, team2_id, team1_seed, team2_seed, best_of FROM public.brkt_matches WHERE version_id = @versionId AND status = 'pending'",
            new { versionId })).AsList();

        int count = 0;
        foreach (var match in matches)
        {
            bool hasT1 = match.team1_id is not null;
            bool hasT2 = match.team2_id is not null;

            if (!(hasT1 ^ hasT2)) continue; // both or neither → skip

            Guid winnerId = hasT1 ? (Guid)match.team1_id : (Guid)match.team2_id;
            int? winnerSeed = hasT1 ? (int?)match.team1_seed : (int?)match.team2_seed;
            int bestOf = (int)(match.best_of ?? 1);
            int winScore = bestOf == 1 ? 13 : (int)Math.Ceiling(bestOf / 2.0);
            int t1Score = hasT1 ? winScore : 0;
            int t2Score = hasT2 ? winScore : 0;

            await conn.ExecuteAsync(@"
                UPDATE public.brkt_matches
                   SET status = 'completed', winner_id = @winnerId, loser_id = null,
                       team1_score = @t1, team2_score = @t2
                 WHERE id = @id",
                new { winnerId, t1 = t1Score, t2 = t2Score, id = (Guid)match.id });

            // Advance winner along edges with their seed
            await AdvanceTeamAsync(conn, (Guid)match.id, versionId, "winner", winnerId, winnerSeed);
            count++;
        }

        return count;
    }

    // ── Reset (keep structure, clear results) ────────────────────────────────

    public async Task ResetAsync(Guid versionId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            var version = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT tournament_id, stage_id FROM public.brkt_versions WHERE id = @versionId FOR UPDATE",
                new { versionId }, tx);
            if (version is null)
            {
                tx.Commit();
                return;
            }

            var tournamentId = (Guid)version.tournament_id;
            var stageId = (Guid?)version.stage_id;

            await ClearTournamentWinnerIfVersionWinnerAsync(conn, tx, versionId, tournamentId, reopenCompleted: true);

            var matches = (await conn.QueryAsync(
                "SELECT id, round_index FROM public.brkt_matches WHERE version_id = @versionId",
                new { versionId }, tx)).AsList();

            var matchIds = matches.Select(m => (Guid)m.id).ToArray();
            await DeleteMatchDerivedRowsAsync(conn, tx, matchIds);

            foreach (var match in matches)
            {
                if ((int)match.round_index == 0)
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE public.brkt_matches
                           SET status = 'pending',
                               winner_id = NULL,
                               loser_id = NULL,
                               team1_score = NULL,
                               team2_score = NULL,
                               scheduled_time = NULL
                         WHERE id = @id
                        """,
                        new { id = (Guid)match.id }, tx);
                }
                else
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE public.brkt_matches
                           SET status = 'pending',
                               team1_id = NULL,
                               team2_id = NULL,
                               team1_seed = NULL,
                               team2_seed = NULL,
                               winner_id = NULL,
                               loser_id = NULL,
                               team1_score = NULL,
                               team2_score = NULL,
                               scheduled_time = NULL
                         WHERE id = @id
                        """,
                        new { id = (Guid)match.id }, tx);
                }
            }

            if (stageId.HasValue)
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE public.tournament_stages
                       SET status = CASE WHEN status::text = 'completed' THEN 'active' ELSE status END,
                           updated_at = NOW()
                     WHERE id = @stageId
                    """,
                    new { stageId = stageId.Value }, tx);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ── Clear (delete entire version) ────────────────────────────────────────

    public async Task ClearAsync(Guid versionId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            var version = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT tournament_id, stage_id FROM public.brkt_versions WHERE id = @versionId FOR UPDATE",
                new { versionId }, tx);
            if (version is null)
            {
                tx.Commit();
                return;
            }

            var tournamentId = (Guid)version.tournament_id;
            var stageId = (Guid?)version.stage_id;

            await ClearTournamentWinnerIfVersionWinnerAsync(conn, tx, versionId, tournamentId, reopenCompleted: true);

            var matchIds = (await conn.QueryAsync<Guid>(
                "SELECT id FROM public.brkt_matches WHERE version_id = @versionId",
                new { versionId }, tx)).ToArray();
            await DeleteMatchDerivedRowsAsync(conn, tx, matchIds);
            await DeleteVersionGraphRowsAsync(conn, tx, versionId);

            await conn.ExecuteAsync("DELETE FROM public.brkt_matches WHERE version_id = @versionId", new { versionId }, tx);
            await conn.ExecuteAsync("DELETE FROM public.brkt_versions WHERE id = @versionId", new { versionId }, tx);

            if (stageId.HasValue)
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE public.tournament_stages
                       SET status = 'draft',
                           updated_at = NOW()
                     WHERE id = @stageId
                    """,
                    new { stageId = stageId.Value }, tx);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task ClearTournamentWinnerIfVersionWinnerAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        Guid versionId,
        Guid tournamentId,
        bool reopenCompleted)
    {
        var winnerCameFromVersion = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.tournaments t
                JOIN public.brkt_matches m ON m.version_id = @versionId
                WHERE t.id = @tournamentId
                  AND t.winner_id IS NOT NULL
                  AND m.winner_id = t.winner_id
            )
            """,
            new { versionId, tournamentId }, tx);

        if (!winnerCameFromVersion)
            return;

        await conn.ExecuteAsync(
            "SELECT public.admin_clear_tournament_winner(@tournamentId, @reopenCompleted)",
            new { tournamentId, reopenCompleted }, tx);
    }

    private static async Task DeleteMatchDerivedRowsAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        Guid[] matchIds)
    {
        if (matchIds.Length == 0)
            return;

        await conn.ExecuteAsync(
            """
            DELETE FROM public.dispute_comments dc
            USING public.tournament_disputes td
            WHERE dc.dispute_id = td.id
              AND td.match_id = ANY(@matchIds)
            """,
            new { matchIds }, tx);
        await conn.ExecuteAsync("DELETE FROM public.tournament_disputes WHERE match_id = ANY(@matchIds)", new { matchIds }, tx);
        await conn.ExecuteAsync("DELETE FROM public.match_disputes WHERE match_id = ANY(@matchIds)", new { matchIds }, tx);
        await conn.ExecuteAsync("DELETE FROM public.match_result_reports WHERE match_id = ANY(@matchIds)", new { matchIds }, tx);
        await conn.ExecuteAsync("DELETE FROM public.match_completed_events WHERE match_id = ANY(@matchIds)", new { matchIds }, tx);
        await conn.ExecuteAsync("DELETE FROM public.brkt_match_games WHERE match_id = ANY(@matchIds)", new { matchIds }, tx);
        await conn.ExecuteAsync("DELETE FROM public.brkt_match_events WHERE match_id = ANY(@matchIds)", new { matchIds }, tx);
    }

    private static async Task DeleteVersionGraphRowsAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        Guid versionId)
    {
        await conn.ExecuteAsync("DELETE FROM public.brkt_layout WHERE version_id = @versionId", new { versionId }, tx);
        await conn.ExecuteAsync("DELETE FROM public.brkt_advancements WHERE version_id = @versionId", new { versionId }, tx);
    }

    // ── Internal: advance a team along edges ─────────────────────────────────

    private static async Task AdvanceTeamAsync(System.Data.IDbConnection conn,
        Guid sourceMatchId, Guid versionId, string edgeType, Guid teamId, int? teamSeed = null)
    {
        var edges = (await conn.QueryAsync(
            "SELECT target_match_id, target_slot FROM public.brkt_advancements WHERE version_id=@v AND source_match_id=@s AND type=@t",
            new { v = versionId, s = sourceMatchId, t = edgeType })).AsList();

        foreach (var edge in edges)
        {
            string teamCol = (int)edge.target_slot == 1 ? "team1_id" : "team2_id";
            string seedCol = (int)edge.target_slot == 1 ? "team1_seed" : "team2_seed";
            await conn.ExecuteAsync(
                $"UPDATE public.brkt_matches SET {teamCol} = @teamId, {seedCol} = @teamSeed WHERE id = @targetId",
                new { teamId, teamSeed, targetId = (Guid)edge.target_match_id });
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

        var matchContext = await CaptainMatchLinkBuilder.ResolveContextAsync(conn, matchId);
        var matchLink = CaptainMatchLinkBuilder.BuildLink(matchContext.TournamentSlug, matchId);

        foreach (var captainId in captainIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@userId, 'match_ready', 'Match Ready',
                        'Your match is ready. Head to the Captain dashboard to start the map veto.',
                        @link,
                        jsonb_build_object(
                            'match_id', @matchId::text,
                            'tournament_slug', @tournamentSlug
                        )::jsonb,
                        false)
                """,
                new
                {
                    userId = captainId,
                    matchId,
                    link = matchLink,
                    tournamentSlug = matchContext.TournamentSlug,
                });
        }
    }
}
