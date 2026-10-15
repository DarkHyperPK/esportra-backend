using System.Data;
using Dapper;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Esportra.Core.Broadcast;

namespace Esportra.Api.Endpoints;

public static partial class BroadcastShowEndpoints
{
    private static async Task<IResult> GetEnrollment(Guid showId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var show = await ReadEnrollment(conn, showId);
        if (show is null) return Results.NotFound();
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, show.OrgId, user.UserIdGuid)) return Results.StatusCode(403);
        var stages = await conn.QueryAsync<EnrollmentStage>("SELECT s.id AS Id, s.name AS Name FROM tournament_stages s JOIN tournaments t ON t.id = s.tournament_id WHERE t.id = @TournamentId AND t.organization_id = @OrgId AND t.deleted_at IS NULL ORDER BY s.stage_order, s.id LIMIT 100", show);
        var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM broadcast_show_matches WHERE show_id = @showId", new { showId });
        return Results.Ok(new { show.Enabled, show.StageIds, show.Revision, show.CheckedAt, show.Error, stages, atCapacity = count >= 200 });
    }

    private static async Task<IResult> SetEnrollment(Guid showId, EnrollmentRequest request, HttpContext ctx, IDbConnectionFactory db, IBroadcastEnrollmentRepository enrollment, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (request.StageIds is null || request.StageIds.Length > 100 || request.StageIds.Any(id => id == Guid.Empty) || request.StageIds.Distinct().Count() != request.StageIds.Length || request.Enabled && request.StageIds.Length == 0 || request.Revision < 0 || request.Revision == int.MaxValue)
            return Results.BadRequest(new { error = "Choose the stages to follow and a valid revision." });
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var show = await ReadEnrollment(conn, showId, tx);
        if (show is null) return Results.NotFound();
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, show.OrgId, user.UserIdGuid, tx)) return Results.StatusCode(403);
        if (show.Status is not ("draft" or "scheduled") || show.Revision != request.Revision) return Results.Conflict(new { error = "Show settings changed. Reload before saving." });
        var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tournament_stages s JOIN tournaments t ON t.id = s.tournament_id WHERE s.id = ANY(@StageIds) AND t.id = @TournamentId AND t.organization_id = @OrgId AND t.deleted_at IS NULL", new { request.StageIds, show.TournamentId, show.OrgId }, tx);
        if (count != request.StageIds.Length) return Results.BadRequest(new { error = "Stages must belong to this show's tournament." });
        await conn.ExecuteAsync("UPDATE broadcast_shows SET follow_bracket = @Enabled, followed_stage_ids = @StageIds, enrollment_revision = enrollment_revision + 1, updated_at = NOW() WHERE id = @showId", new { showId, request.Enabled, request.StageIds }, tx);
        tx.Commit();
        var result = request.Enabled ? await enrollment.EnrollAsync(showId, ct) : new BroadcastEnrollmentResult(0, false);
        return Results.Ok(new { revision = show.Revision + 1, result.Added, result.AtCapacity });
    }

    private static Task<EnrollmentShow?> ReadEnrollment(IDbConnection conn, Guid showId, IDbTransaction? tx = null) =>
        conn.QuerySingleOrDefaultAsync<EnrollmentShow>("SELECT org_id AS OrgId, tournament_id AS TournamentId, status AS Status, follow_bracket AS Enabled, followed_stage_ids AS StageIds, enrollment_revision AS Revision, enrollment_checked_at AS CheckedAt, enrollment_error AS Error FROM broadcast_shows WHERE id = @showId" + (tx is null ? "" : " FOR UPDATE"), new { showId }, tx);
    // Npgsql reports uuid[] as System.Array; property mapping avoids constructor type matching.
    private sealed class EnrollmentShow
    {
        public Guid OrgId { get; init; }
        public Guid? TournamentId { get; init; }
        public string Status { get; init; } = "";
        public bool Enabled { get; init; }
        public Guid[] StageIds { get; init; } = [];
        public int Revision { get; init; }
        public DateTime? CheckedAt { get; init; }
        public string? Error { get; init; }
    }
    private sealed record EnrollmentStage(Guid Id, string Name);
}

public sealed record EnrollmentRequest(bool Enabled, Guid[]? StageIds, int Revision);
