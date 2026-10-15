using System.Data;
using Dapper;
using Esportra.Contracts.Database;
using Esportra.Core.Broadcast;
using Esportra.Core.Tournaments;
using Microsoft.Extensions.Logging;

namespace Esportra.Infrastructure.Database;

public sealed class BroadcastEnrollmentRepository(IDbConnectionFactory db, ILogger<BroadcastEnrollmentRepository> logger) : IBroadcastEnrollmentRepository
{
    public async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var shows = await conn.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM broadcast_shows WHERE follow_bracket AND status IN ('draft', 'scheduled', 'live') ORDER BY enrollment_checked_at NULLS FIRST, id LIMIT 25", cancellationToken: ct));
        foreach (var show in shows)
        {
            try { await EnrollAsync(show, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Broadcast enrollment failed for show {ShowId}", show);
                await conn.ExecuteAsync(new CommandDefinition("UPDATE broadcast_shows SET enrollment_checked_at = NOW(), enrollment_error = 'Could not refresh these stages. Retrying; the selected match is retained.' WHERE id = @show", new { show }, cancellationToken: ct));
            }
        }
    }

    public async Task<BroadcastEnrollmentResult> EnrollAsync(Guid showId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var show = await conn.QuerySingleOrDefaultAsync<FollowedShow>(new CommandDefinition("SELECT org_id AS OrgId, tournament_id AS TournamentId, followed_stage_ids AS Stages FROM broadcast_shows WHERE id = @showId AND follow_bracket AND status IN ('draft', 'scheduled', 'live') FOR UPDATE SKIP LOCKED", new { showId }, tx, cancellationToken: ct));
        if (show is null) return new(0, false);
        var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM broadcast_show_matches WHERE show_id = @showId", new { showId }, tx, cancellationToken: ct));
        var added = count >= 200 ? 0 : await InsertEligible(conn, tx, showId, show, (int)(200 - count), ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE broadcast_shows SET enrollment_checked_at = NOW(), enrollment_error = NULL, updated_at = CASE WHEN @added > 0 THEN NOW() ELSE updated_at END WHERE id = @showId", new { showId, added }, tx, cancellationToken: ct));
        tx.Commit();
        return new(added, count + added >= 200);
    }

    private static Task<int> InsertEligible(IDbConnection conn, IDbTransaction tx, Guid showId, FollowedShow show, int remaining, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition($"""
            WITH eligible AS (
                SELECT bm.id, bm.scheduled_time FROM brkt_matches bm JOIN brkt_versions bv ON bv.id = bm.version_id
                JOIN tournaments t ON t.id = bv.tournament_id
                {BracketTeamResolutionSql.BracketMatchTeam1Joins}
                {BracketTeamResolutionSql.BracketMatchTeam2Joins}
                WHERE t.id = @TournamentId AND t.organization_id = @OrgId AND t.deleted_at IS NULL
                  AND bv.stage_id = ANY(@Stages) AND bv.status IN ('published', 'active')
                  AND bm.status IN ('pending', 'scheduled', 'in_progress') AND bm.winner_id IS NULL
                  AND bm.team1_id IS NOT NULL AND bm.team2_id IS NOT NULL
                  AND NULLIF(BTRIM(COALESCE(t1.name, tp1.team_name, sp1.username)), '') IS NOT NULL
                  AND NULLIF(BTRIM(COALESCE(t2.name, tp2.team_name, sp2.username)), '') IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM broadcast_show_matches sm WHERE sm.show_id = @showId AND sm.match_id = bm.id)
                ORDER BY bm.scheduled_time NULLS LAST, bm.id LIMIT @remaining
            )
            INSERT INTO broadcast_show_matches (show_id, match_id, match_code, display_order)
            SELECT @showId, id, 'M_' || UPPER(LEFT(REPLACE(gen_random_uuid()::text, '-', ''), 18)),
                   COALESCE((SELECT MAX(display_order) FROM broadcast_show_matches WHERE show_id = @showId), -1) + ROW_NUMBER() OVER (ORDER BY scheduled_time NULLS LAST, id)
            FROM eligible ON CONFLICT DO NOTHING
            """, new { showId, show.TournamentId, show.OrgId, show.Stages, remaining }, tx, cancellationToken: ct));

    private sealed class FollowedShow
    {
        public Guid OrgId { get; init; }
        public Guid? TournamentId { get; init; }
        public Guid[] Stages { get; init; } = [];
    }
}
