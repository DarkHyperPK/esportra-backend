using System.Text.Json;
using System.Text.RegularExpressions;
using Esportra.Api.Services;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Air game packs (20 Nov): a game's official pictures for broadcast graphics. Air asks for the manifest and
/// gets a short-lived download link per file; files are content-addressed (<c>{game}/files/{sha256}.{ext}</c>).
/// Only signed-in accounts with active Air access receive links.
/// </summary>
public static partial class AirGamePackEndpoints
{
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    public static void MapAirGamePackEndpoints(this WebApplication app)
    {
        app.MapGet("/api/air/game-packs/{game}", Get).RequireAuthorization("Authenticated");
    }

    [GeneratedRegex("^[a-z0-9]{2,16}$")] private static partial Regex GameId();
    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex Sha256();

    private static async Task<IResult> Get(string game, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts, IGamePackStore store, CancellationToken ct)
    {
        if (!GameId().IsMatch(game)) return Results.NotFound(new { error = "There is no pack for this game." });
        using (var connection = db.CreateConnection())
        {
            var userId = AirAccountEndpoints.User(ctx);
            if (userId == Guid.Empty || !await accounts.HasAccessAsync(connection, userId))
                return Results.Json(new { error = "Active Air access is required to download game packs." }, statusCode: 403);
        }
        var json = await store.ReadManifestAsync(game, ct);
        if (json is null) return Results.NotFound(new { error = "There is no pack for this game yet." });
        IReadOnlyDictionary<string, string> files;
        try { files = FilePaths(game, json); }
        catch (JsonException) { return Results.Problem("The game pack is not available right now.", statusCode: 503); }
        var signed = await store.SignAsync(files.Values.ToArray(), LinkLifetime, ct);
        var urls = files.Where(f => signed.ContainsKey(f.Value)).ToDictionary(f => f.Key, f => signed[f.Value]);
        ctx.Response.Headers.CacheControl = "no-store";
        using var manifest = JsonDocument.Parse(json);
        return Results.Ok(new { manifest = manifest.RootElement.Clone(), urls });
    }

    /// <summary>
    /// The storage path of every file the manifest names (sha256 → path). Only well-formed entries of the right
    /// game are signed, so a bad manifest can never hand out links to anything else in the bucket.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FilePaths(string game, string manifestJson)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("game", out var g) || g.GetString() != game) throw new JsonException("The manifest is for another game.");
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) throw new JsonException("The manifest lists no assets.");
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var asset in assets.EnumerateArray().Take(2000))
        {
            foreach (var name in new[] { "file", "thumb" })
            {
                if (!asset.TryGetProperty(name, out var f) || f.ValueKind != JsonValueKind.Object) continue;
                var sha = f.TryGetProperty("sha256", out var s) ? s.GetString() : null;
                var ext = f.TryGetProperty("ext", out var e) ? e.GetString() : null;
                if (sha is null || !Sha256().IsMatch(sha) || ext is not ("png" or "jpg" or "webp")) continue;
                paths[sha] = $"{game}/files/{sha}.{ext}";
            }
        }
        return paths;
    }
}
