using System.Text.Json;
using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Esportra.Core.Air;
using Esportra.Core.Tournaments;

namespace Esportra.Api.Endpoints;

public static partial class AirBroadcastEndpoints
{
    private static async Task<IResult> GetDraft(Guid matchId, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        using var conn = db.CreateConnection();
        if (!await AuthorizedDevice(conn, ctx, accounts)) return Results.Forbid();
        if ((await Matches(conn, AirAccountEndpoints.User(ctx), matchId, history: true)).Length == 0) return Results.NotFound();
        var saved = await conn.QuerySingleOrDefaultAsync<ProductionDraftRow>("SELECT revision,document::text AS Document FROM air_production_drafts WHERE owner_id=@userId AND match_id=@matchId", new { userId = AirAccountEndpoints.User(ctx), matchId });
        return Results.Ok(new { revision = saved?.Revision ?? 0, document = JsonSerializer.Deserialize<JsonElement>(saved?.Document ?? "{}") });
    }

    private static async Task<IResult> SaveDraft(Guid matchId, ProductionDraftRequest request, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        if (request.Revision is < 0 or > 1000000 || AirProductionDraft.Validate(request.Document) is { } error)
            return Results.BadRequest(new { error = "Use valid broadcast-only production settings and revision." });
        using var conn = db.CreateConnection();
        if (!await AuthorizedDevice(conn, ctx, accounts)) return Results.Forbid();
        if ((await Matches(conn, AirAccountEndpoints.User(ctx), matchId)).Length == 0) return Results.NotFound();
        var assetIds = request.Document.EnumerateObject().SelectMany(p => p.Value.EnumerateArray())
            .Where(row => row.TryGetProperty("assetId", out _)).Select(row => Guid.Parse(row.GetProperty("assetId").GetString()!)).Distinct().ToArray();
        if (assetIds.Length > 0 && await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM air_assets a JOIN air_workspaces w ON w.id=a.workspace_id WHERE a.id=ANY(@assetIds) AND w.owner_id=@userId", new { assetIds, userId = AirAccountEndpoints.User(ctx) }) != assetIds.Length)
            return Results.BadRequest(new { error = "Upload these sponsor assets to your own Air workspace first." });
        var saved = await conn.QuerySingleOrDefaultAsync<ProductionDraftRow>("""
            INSERT INTO air_production_drafts(owner_id,match_id,document,revision)
            SELECT @userId,@matchId,CAST(@document AS jsonb),1 WHERE @revision=0
            ON CONFLICT(owner_id,match_id) DO UPDATE SET document=excluded.document,revision=air_production_drafts.revision+1,updated_at=now()
            WHERE air_production_drafts.revision=@revision
            RETURNING revision,document::text AS Document
            """, new { userId = AirAccountEndpoints.User(ctx), matchId, document = request.Document.GetRawText(), revision = request.Revision });
        // UPDATE handles established rows: INSERT's initial-revision guard does
        // not run for nonzero revisions.
        if (saved is null && request.Revision > 0)
            saved = await conn.QuerySingleOrDefaultAsync<ProductionDraftRow>("UPDATE air_production_drafts SET document=CAST(@document AS jsonb),revision=revision+1,updated_at=now() WHERE owner_id=@userId AND match_id=@matchId AND revision=@revision RETURNING revision,document::text AS Document", new { userId = AirAccountEndpoints.User(ctx), matchId, document = request.Document.GetRawText(), revision = request.Revision });
        return saved is null ? Results.Conflict(new { error = "Production settings changed elsewhere. Reload and review before saving." }) : Results.Ok(new { revision = saved.Revision, document = request.Document });
    }

    private static async Task<IResult> GetBracket(Guid matchId, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts, IEnumerable<IStandingsResolver> resolvers, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        if (!await AuthorizedDevice(conn, ctx, accounts)) return Results.Forbid();
        var match = (await Matches(conn, AirAccountEndpoints.User(ctx), matchId, history: true)).SingleOrDefault();
        return match is null ? Results.NotFound() : Results.Ok(await BroadcastBracketPresentationEndpoints.ReadAsync(conn, null, match.TournamentId, resolvers, ct));
    }

    private sealed record ProductionDraftRow(long Revision, string Document);
}

public sealed record ProductionDraftRequest(long Revision, JsonElement Document);
