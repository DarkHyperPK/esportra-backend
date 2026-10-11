using System.Data;
using Dapper;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

internal static class AdminPublicToolsEndpoints
{
    private const int PageSize = 50;

    public static void MapAdminPublicToolsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/public-tools/veto-sessions", ListVetoSessionsAsync)
            .RequireAuthorization("Authenticated")
            .WithMetadata(new RateLimitPolicyMetadata("admin"));
    }

    private static async Task<IResult> ListVetoSessionsAsync(
        HttpContext ctx, IDbConnectionFactory db, CancellationToken ct, [AsParameters] AdminPublicVetoQuery query)
    {
        var (page, game) = query;
        if (ctx.Items["UserContext"] is not UserContext { IsSuperAdmin: true })
            return Results.Forbid();
        if (page < 1 || page > int.MaxValue / PageSize + 1)
            return Results.BadRequest(new { error = "Page must be a positive number within the supported range." });
        if (game is not null and not ("valorant" or "cs2" or "r6s"))
            return Results.BadRequest(new { error = "Game must be valorant, cs2, or r6s." });

        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction(IsolationLevel.RepeatableRead);
        var summary = await conn.QuerySingleAsync<PublicVetoUsageSummary>(new CommandDefinition(
            """
            SELECT count(*) AS TotalSessions,
                   count(*) FILTER (WHERE status = 'completed') AS CompletedSessions,
                   count(*) FILTER (WHERE overlay_first_loaded_at IS NOT NULL) AS OverlaySessions
            FROM public.public_veto_sessions
            WHERE (@game IS NULL OR game = @game)
            """, new { game }, transaction: tx, cancellationToken: ct));
        var sessions = (await conn.QueryAsync<AdminPublicVetoSession>(new CommandDefinition(
            """
            SELECT id AS Id, game AS Game, best_of AS BestOf,
                   team1_name AS Team1Name, team2_name AS Team2Name, status AS Status,
                   created_at AS CreatedAt, expires_at AS ExpiresAt, completed_at AS CompletedAt,
                   overlay_first_loaded_at AS OverlayAccessedAt
            FROM public.public_veto_sessions
            WHERE (@game IS NULL OR game = @game)
            ORDER BY created_at DESC, id DESC
            LIMIT @pageSize OFFSET @offset
            """, new { game, pageSize = PageSize, offset = (page - 1) * PageSize }, transaction: tx, cancellationToken: ct))).AsList();
        tx.Commit();
        return Results.Ok(new AdminPublicVetoSessionsResponse(sessions, page, PageSize, summary.TotalSessions, summary));
    }
}

internal sealed record AdminPublicVetoQuery(int Page = 1, string? Game = null);

internal sealed record AdminPublicVetoSession(
    Guid Id, string Game, int BestOf, string Team1Name, string Team2Name, string Status,
    DateTime CreatedAt, DateTime ExpiresAt, DateTime? CompletedAt, DateTime? OverlayAccessedAt);

internal sealed record PublicVetoUsageSummary(long TotalSessions, long CompletedSessions, long OverlaySessions);

internal sealed record AdminPublicVetoSessionsResponse(
    IReadOnlyList<AdminPublicVetoSession> Sessions, int Page, int PageSize, long Total, PublicVetoUsageSummary Summary);
