using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Esportra.Core.Broadcast;

namespace Esportra.Api.Endpoints;

public static partial class BroadcastShowEndpoints
{
    private static readonly JsonSerializerOptions AppearanceJson = new(JsonSerializerDefaults.Web);

    internal static BroadcastAppearance ReadAppearance(string? json)
    {
        try
        {
            var appearance = JsonSerializer.Deserialize<BroadcastAppearance>(json ?? "{}", AppearanceJson);
            return appearance?.Validate() is null && appearance is not null ? appearance : new();
        }
        catch (JsonException) { return new(); }
    }

    private static async Task<IResult> GetAppearance(Guid showId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var show = await conn.QuerySingleOrDefaultAsync<AppearanceShow>("SELECT org_id AS OrgId, status AS Status, appearance::text AS Appearance FROM broadcast_shows WHERE id = @showId", new { showId });
        if (show is null) return Results.NotFound();
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, show.OrgId, user.UserIdGuid)) return Results.StatusCode(403);
        return Results.Ok(ReadAppearance(show.Appearance));
    }

    private static async Task<IResult> SaveAppearance(Guid showId, BroadcastAppearance appearance, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (appearance.Validate() is { } error) return Results.BadRequest(new { error });
        if (appearance.Revision == int.MaxValue) return Results.BadRequest(new { error = "Appearance revision limit reached." });
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var show = await conn.QuerySingleOrDefaultAsync<AppearanceShow>("SELECT org_id AS OrgId, status AS Status, appearance::text AS Appearance FROM broadcast_shows WHERE id = @showId FOR UPDATE", new { showId }, tx);
        if (show is null) return Results.NotFound();
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, show.OrgId, user.UserIdGuid, tx)) return Results.StatusCode(403);
        if (show.Status is not ("draft" or "scheduled")) return Results.Conflict(new { error = "Stop the show before editing its appearance." });
        if (ReadAppearance(show.Appearance).Revision != appearance.Revision) return Results.Conflict(new { error = "Appearance changed in another window. Reload before saving." });
        var ids = appearance.Matches?.Keys.ToArray() ?? [];
        var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM broadcast_show_matches WHERE show_id = @showId AND match_id = ANY(@ids)", new { showId, ids }, tx);
        if (count != ids.Length) return Results.BadRequest(new { error = "Display overrides must belong to this show's matches." });
        appearance = appearance with { Revision = appearance.Revision + 1 };
        await conn.ExecuteAsync("UPDATE broadcast_shows SET appearance = @json::jsonb, updated_at = NOW() WHERE id = @showId", new { showId, json = JsonSerializer.Serialize(appearance, AppearanceJson) }, tx);
        tx.Commit();
        return Results.Ok(appearance);
    }

    private static async Task<IResult> GetNodeMatchContext(Guid showId, Guid matchId, HttpContext ctx, IDbConnectionFactory db)
    {
        var node = await BroadcastHelpers.ValidateNodeBearerAsync(ctx, db);
        if (node is null) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var show = await conn.QuerySingleOrDefaultAsync<ShowRow>("SELECT id AS Id, org_id AS OrgId, title AS Title, status AS Status FROM broadcast_shows WHERE id = @showId AND org_id = @orgId AND status NOT IN ('ended', 'cancelled') AND published_configuration IS NOT NULL", new { showId, orgId = node.OrgId });
        if (show is null) return Results.NotFound();
        var configuration = await BuildShowConfigPayloadAsync(conn, show, showId, matchId);
        var match = configuration.Matches.FirstOrDefault(item => item.MatchId == matchId.ToString());
        if (match?.Status == "completed") return Results.Conflict(new { error = "The selected match is completed. Final series metadata is refreshed; choose the next match when ready.", eligible = false, showId, selectedMatchId = matchId, match, configuration.MatchHistory, matches = configuration.Matches.Where(item => item.Status != "completed").ToArray() });
        if (match is null) return Results.Conflict(new { error = "The selected match is no longer eligible. Current output is retained; choose the next match when ready.", eligible = false, showId, selectedMatchId = matchId, configuration.Matches });
        // No unpublished rundown or other match is returned. This read cannot advance production.
        return Results.Ok(new { showId, match, configuration.Matches, configuration.EventLabel, configuration.TournamentId, configuration.Game, configuration.Casters, configuration.Sponsors, configuration.MapPool, configuration.Appearance, configuration.MatchHistory, schedule = configuration.Matches.Select(item => new { item.MatchId, item.ScheduledAt, item.Status, teams = item.Teams?.Select(team => team.Name), item.BestOf }) });
    }

    private sealed record AppearanceShow(Guid OrgId, string Status, string Appearance);
}
