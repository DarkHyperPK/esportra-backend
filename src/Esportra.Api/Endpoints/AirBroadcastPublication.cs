using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Esportra.Api.Services;
using Esportra.Core.Air;
using Esportra.Core.Broadcast;
using Esportra.Core.Tournaments;

namespace Esportra.Api.Endpoints;

public static partial class AirBroadcastEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static async Task<IResult> Prepare(Guid matchId, AirPrepareRequest request, HttpContext ctx,
        IDbConnectionFactory db, AirAccountService accounts, IEnumerable<IStandingsResolver> resolvers, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty) return Results.BadRequest(new { error = "Provide a preparation request identity." });
        using var conn = db.CreateConnection();
        if (!await AuthorizedDevice(conn, ctx, accounts)) return Results.Json(new { error = "An active Air account and registered production PC are required. Check access in Settings & help." }, statusCode: 403);
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        var userId = AirAccountEndpoints.User(ctx);
        var match = (await Matches(conn, userId, matchId, tx)).SingleOrDefault();
        if (match is null) return Results.NotFound(new { error = "This match is unavailable or you no longer have broadcast access." });
        var packId = AirGamePacks.Resolve(match.Game);
        if (packId is null) return Results.Conflict(new { error = "HUD not available for this game.", game = match.Game });
        await conn.ExecuteAsync("INSERT INTO air_match_packages(owner_id,match_id) VALUES(@userId,@matchId) ON CONFLICT(owner_id,match_id) DO NOTHING", new { userId, matchId }, tx);
        var package = await conn.QuerySingleAsync<PackageRow>("SELECT id,latest_version AS LatestVersion FROM air_match_packages WHERE owner_id=@userId AND match_id=@matchId FOR UPDATE", new { userId, matchId }, tx);
        var prior = await Read(conn, package.Id, null, request.RequestId, tx);
        if (prior is not null) { tx.Commit(); return Results.Ok(prior); }
        var configuration = await BuildConfiguration(conn, package.Id, match, resolvers, ct, tx);
        var sourceHash = Hash(configuration.ToJsonString(JsonOptions));
        var latest = await conn.QuerySingleOrDefaultAsync<string>("SELECT source_hash FROM air_match_package_versions WHERE package_id=@id AND version=@version", new { id = package.Id, version = package.LatestVersion }, tx);
        if (latest == sourceHash) { var unchanged = await Read(conn, package.Id, package.LatestVersion, null, tx); tx.Commit(); return Results.Ok(unchanged); }
        var version = checked(package.LatestVersion + 1);
        configuration["rundownVersion"] = version;
        configuration["packageVersion"] = version;
        var json = configuration.ToJsonString(JsonOptions);
        await conn.ExecuteAsync("INSERT INTO air_match_package_versions(package_id,version,request_id,source_hash,content_hash,configuration) VALUES(@id,@version,@requestId,@sourceHash,@hash,@json); UPDATE air_match_packages SET latest_version=@version WHERE id=@id", new { id = package.Id, version, requestId = request.RequestId, sourceHash, hash = Hash(json), json }, tx);
        var result = await Read(conn, package.Id, version, null, tx);
        tx.Commit();
        return Results.Ok(result);
    }
    private static async Task<JsonObject> BuildConfiguration(IDbConnection conn, Guid packageId, AirMatch match, IEnumerable<IStandingsResolver> resolvers, CancellationToken ct, IDbTransaction tx)
    {
        var rosters = await BroadcastShowEndpoints.ReadRegisteredRosters(conn, match.TournamentId, [match.Team1Id, match.Team2Id]);
        var teams = new[] {
            new ShowTeamSlot(match.TeamA,match.TagA,match.LogoA,match.ScoreA,match.Team1Id,rosters.GetValueOrDefault(match.Team1Id) ?? []),
            new ShowTeamSlot(match.TeamB,match.TagB,match.LogoB,match.ScoreB,match.Team2Id,rosters.GetValueOrDefault(match.Team2Id) ?? [])
        };
        var actions = await conn.QueryAsync<BroadcastVetoAction>("SELECT a.action_number AS ActionNumber,a.action_type AS Action,gm.map_name AS Map,a.team_side AS TeamSide,a.side AS Side FROM match_map_veto_actions a LEFT JOIN game_maps gm ON gm.id=a.map_id WHERE a.match_id=@id ORDER BY a.action_number LIMIT 100", new { id = match.Id });
        var veto = BroadcastVetoProjection.Project(actions);
        var maps = (await conn.QueryAsync<string>("SELECT gm.map_name FROM tournament_map_pools p JOIN game_maps gm ON gm.id=p.map_id WHERE p.tournament_id=@id ORDER BY gm.map_name LIMIT 100", new { id = match.TournamentId })).ToArray();
        var code = "AIR_" + packageId.ToString("N")[..16].ToUpperInvariant();
        var slot = new ShowMatchSlot(match.Id.ToString(), code, 0, match.Status, teams, match.BestOf, veto, match.ScheduledAt);
        var screens = AirGamePacks.ValorantScreens.Select(s => new ShowRundownSlot(AirGamePacks.ScreenId(packageId, s.Overlay).ToString(), s.Title, s.Type, null, null, s.Overlay, match.Id.ToString(), new BroadcastSegmentPlan("manual", "manual", "manual", 30, [s.Overlay], Transition: new("fade", "Fade", 300)))).ToArray();
        var config = new ShowConfigPayload(packageId.ToString(), match.TournamentName, "draft", [slot], [], [], screens, teams, match.TournamentName, match.BestOf, match.TournamentId.ToString(), Game: "valorant", Veto: veto, MapPool: maps, PreferredMatchCode: code);
        var node = JsonSerializer.SerializeToNode(config, JsonOptions)!.AsObject();
        node["packageId"] = packageId.ToString(); node["packId"] = AirGamePacks.Resolve(match.Game);
        node["source"] = "esportra"; node["accountPackage"] = true; node["schemaVersion"] = 1;
        var draft = await conn.QuerySingleOrDefaultAsync<ProductionDraftRow>("SELECT revision,document::text AS Document FROM air_production_drafts WHERE owner_id=(SELECT owner_id FROM air_match_packages WHERE id=@packageId) AND match_id=@matchId", new { packageId, matchId = match.Id }, tx);
        if (draft is not null)
        {
            var document = JsonNode.Parse(draft.Document)!.AsObject();
            node["sponsors"] = document["sponsors"]?.DeepClone() ?? new JsonArray();
            node["casters"] = document["talent"]?.DeepClone() ?? new JsonArray();
            node["crew"] = document["crew"]?.DeepClone() ?? new JsonArray();
            node["productionRevision"] = draft.Revision;
        }
        node["sceneAutomationEnabled"] = false;
        node["bracketSnapshot"] = JsonSerializer.SerializeToNode(await BroadcastBracketPresentationEndpoints.ReadAsync(conn, null, match.TournamentId, resolvers, ct), JsonOptions);
        // Retrieval time is not package content. Keeping it in the source hash
        // would publish a new version on every unchanged preparation.
        node["bracketSnapshot"]?.AsObject().Remove("fetchedAt");
        foreach (var (entry, index) in node["sponsors"]!.AsArray().Select((entry, index) => (entry!, index)))
        {
            entry["sponsorId"] = AirGamePacks.ScreenId(packageId, "sponsor-" + index).ToString();
            if (entry["assetId"] is { } asset) entry["logoUrl"] = "/air-assets/" + asset.GetValue<string>();
        }
        foreach (var entry in node["casters"]!.AsArray()) entry!["displayName"] = entry["name"]!.DeepClone();
        node["assets"] = new JsonObject { ["version"] = 1, ["strategy"] = "pin-on-download", ["fonts"] = "bundled", ["hud"] = "bundled" };

        return node;
    }
    private static async Task<IResult> ReadVersion(Guid packageId, int version, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        if (version < 1) return Results.BadRequest();
        using var conn = db.CreateConnection();
        if (!await AuthorizedDevice(conn, ctx, accounts)) return Results.Json(new { error = "An active Air account and registered production PC are required. Check access in Settings & help." }, statusCode: 403);
        var matchId = await conn.QuerySingleOrDefaultAsync<Guid?>("SELECT match_id FROM air_match_packages WHERE id=@packageId AND owner_id=@userId", new { packageId, userId = AirAccountEndpoints.User(ctx) });
        if (matchId is null || (await Matches(conn, AirAccountEndpoints.User(ctx), matchId, history: true)).Length == 0) return Results.NotFound();
        var result = await Read(conn, packageId, version, null);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    private static Task<AirPublishedPackage?> Read(IDbConnection conn, Guid id, int? version, Guid? request, IDbTransaction? tx = null) =>
        conn.QuerySingleOrDefaultAsync<AirPublishedPackage>("SELECT package_id AS Id,version,content_hash AS Checksum,configuration AS ConfigurationJson,created_at AS CreatedAt FROM air_match_package_versions WHERE package_id=@id AND (@version IS NULL OR version=@version) AND (@request IS NULL OR request_id=@request) ORDER BY version DESC LIMIT 1", new { id, version, request }, tx);
    private sealed record PackageRow(Guid Id, int LatestVersion);
}
public sealed record AirPrepareRequest(Guid RequestId);
public sealed record AirPublishedPackage(Guid Id, int Version, string Checksum, string ConfigurationJson, DateTime CreatedAt);
