using System.Text.Json;
using Esportra.Infrastructure.Integrations;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Services;

/// <summary>One player in a Riot match record: the full Riot ID the scoreboard screenshot doesn't show.</summary>
public sealed record RiotMatchPlayer(string Puuid, string GameName, string TagLine, string TeamId, int Kills, int Deaths, int Assists)
{
    public string RiotId => $"{GameName}#{TagLine}";
}

public sealed record RiotMatch(string MatchId, string Map, string QueueId, int BlueRounds, int RedRounds, DateTimeOffset StartedAt, IReadOnlyList<RiotMatchPlayer> Players);

public enum RiotLookupStatus
{
    Found,
    NotFound,
    /// <summary>The API key can't read Valorant matches (or none is set): retrying won't help.</summary>
    Unavailable,
    /// <summary>Rate limited or Riot is down: try again later.</summary>
    Retry,
}

public sealed record RiotMatchLookupResult(RiotLookupStatus Status, RiotMatch? Match = null);

/// <summary>What a custom game must look like to be the one a screenshot shows.</summary>
public sealed record RiotMatchQuery(string? Map, int ScoreA, int ScoreB, DateTimeOffset Around, TimeSpan Window);

public interface IRiotMatchLookup
{
    /// <summary>The custom game on this map with this score, from the first of these players' match histories that has it.</summary>
    Task<RiotMatchLookupResult> FindCustomGameAsync(IReadOnlyList<string> puuids, RiotMatchQuery query, CancellationToken ct);
}

/// <summary>
/// Finds a custom (tournament) game in linked players' Riot match history (VAL-MATCH-V1). Only matchlist
/// entries that are custom games inside the time window are fetched in full.
/// </summary>
public sealed class RiotMatchLookup(RiotApiClient riot, HybridCache cache, IConfiguration config, ILogger<RiotMatchLookup> logger) : IRiotMatchLookup
{
    private const int MaxDetailsPerPlayer = 8;

    public async Task<RiotMatchLookupResult> FindCustomGameAsync(IReadOnlyList<string> puuids, RiotMatchQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config["Riot:ApiKey"])) return new RiotMatchLookupResult(RiotLookupStatus.Unavailable);
        var sawRetry = false;
        foreach (var puuid in puuids)
        {
            var shard = await RiotShards.ResolveAsync(riot, cache, puuid, null, ct);
            var (status, body) = await riot.ProxyAsync(shard, $"/val/match/v1/matchlists/by-puuid/{Uri.EscapeDataString(puuid)}", ct);
            if (status is 401 or 403) return new RiotMatchLookupResult(RiotLookupStatus.Unavailable);
            if (status is 429 or >= 500) { sawRetry = true; continue; }
            if (status != 200) continue;

            foreach (var matchId in CandidateIds(body, query).Take(MaxDetailsPerPlayer))
            {
                var (detailStatus, detail) = await riot.ProxyAsync(shard, $"/val/match/v1/matches/{Uri.EscapeDataString(matchId)}", ct);
                if (detailStatus is 429 or >= 500) { sawRetry = true; break; }
                if (detailStatus != 200) continue;
                try
                {
                    using var doc = JsonDocument.Parse(detail);
                    var match = ParseMatch(doc.RootElement);
                    if (Fits(match, query)) return new RiotMatchLookupResult(RiotLookupStatus.Found, match);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "Could not read Riot match {MatchId}", matchId);
                }
            }
        }
        return new RiotMatchLookupResult(sawRetry ? RiotLookupStatus.Retry : RiotLookupStatus.NotFound);
    }

    /// <summary>Custom games in the matchlist that started inside the window, closest first.</summary>
    public static IEnumerable<string> CandidateIds(string matchlistJson, RiotMatchQuery query)
    {
        using var doc = JsonDocument.Parse(matchlistJson);
        if (!doc.RootElement.TryGetProperty("history", out var history) || history.ValueKind != JsonValueKind.Array) return [];
        return history.EnumerateArray()
            .Select(e => (
                Id: e.TryGetProperty("matchId", out var id) ? id.GetString() : null,
                Queue: e.TryGetProperty("queueId", out var q) ? q.GetString() : null,
                Start: e.TryGetProperty("gameStartTimeMillis", out var s) && s.TryGetInt64(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null))
            .Where(e => e.Id is not null && (e.Queue is null || string.Equals(e.Queue, "custom", StringComparison.OrdinalIgnoreCase)))
            .Where(e => e.Start is null || (e.Start.Value - query.Around).Duration() <= query.Window)
            .OrderBy(e => e.Start is null ? TimeSpan.MaxValue : (e.Start.Value - query.Around).Duration())
            .Select(e => e.Id!)
            .ToList();
    }

    public static bool Fits(RiotMatch match, RiotMatchQuery query) =>
        string.Equals(match.QueueId, "custom", StringComparison.OrdinalIgnoreCase)
        && (query.Map is null || string.Equals(match.Map, query.Map, StringComparison.OrdinalIgnoreCase))
        && ((match.BlueRounds == query.ScoreA && match.RedRounds == query.ScoreB) || (match.BlueRounds == query.ScoreB && match.RedRounds == query.ScoreA))
        && (match.StartedAt - query.Around).Duration() <= query.Window;

    public static RiotMatch ParseMatch(JsonElement root)
    {
        var info = root.GetProperty("matchInfo");
        int blue = 0, red = 0;
        foreach (var team in root.GetProperty("teams").EnumerateArray())
        {
            var rounds = team.GetProperty("roundsWon").GetInt32();
            if (team.GetProperty("teamId").GetString() == "Blue") blue = rounds;
            else red = rounds;
        }
        var players = root.GetProperty("players").EnumerateArray().Select(p =>
        {
            var stats = p.TryGetProperty("stats", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
            int Stat(string name) => stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
            return new RiotMatchPlayer(
                p.GetProperty("puuid").GetString() ?? "",
                p.TryGetProperty("gameName", out var g) ? g.GetString() ?? "" : "",
                p.TryGetProperty("tagLine", out var t) ? t.GetString() ?? "" : "",
                p.TryGetProperty("teamId", out var team) ? team.GetString() ?? "" : "",
                Stat("kills"), Stat("deaths"), Stat("assists"));
        }).ToList();
        return new RiotMatch(
            info.GetProperty("matchId").GetString() ?? "",
            ValorantMapIds.Resolve(info.GetProperty("mapId").GetString() ?? ""),
            info.TryGetProperty("queueId", out var queue) ? queue.GetString() ?? "" : "",
            blue,
            red,
            DateTimeOffset.FromUnixTimeMilliseconds(info.GetProperty("gameStartMillis").GetInt64()),
            players);
    }
}

/// <summary>The Valorant shard a player's matches live on (cached; it rarely changes).</summary>
public static class RiotShards
{
    public static async Task<string> ResolveAsync(RiotApiClient riot, HybridCache cache, string puuid, string? storedRegion, CancellationToken ct) =>
        await cache.GetOrCreateAsync(
            $"riot:shard:{puuid}",
            async _ =>
            {
                var (status, body) = await riot.ProxyAsync("americas", $"/riot/account/v1/active-shards/by-game/val/by-puuid/{puuid}", ct);
                if (status == 200)
                {
                    using var doc = JsonDocument.Parse(body);
                    var shard = doc.RootElement.GetProperty("activeShard").GetString()?.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(shard)) return shard;
                }
                return storedRegion?.ToLowerInvariant() switch
                {
                    "na" or "br" or "latam" or "kr" or "ap" or "eu" => storedRegion.ToLowerInvariant(),
                    "americas" => "na",
                    "europe" => "eu",
                    "asia" => "ap",
                    _ => "eu",
                };
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(30) },
            cancellationToken: ct) ?? "eu";
}

/// <summary>Riot's internal map paths to display names.</summary>
public static class ValorantMapIds
{
    public static string Resolve(string riotMapId)
    {
        // Riot uses internal paths like /Game/Maps/Triad/Triad
        var map = Maps.GetValueOrDefault(riotMapId);
        if (map is not null) return map;

        // Fallback: extract last path segment
        var lastSlash = riotMapId.LastIndexOf('/');
        return lastSlash >= 0 ? riotMapId[(lastSlash + 1)..] : riotMapId;
    }

    private static readonly Dictionary<string, string> Maps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/Game/Maps/Ascent/Ascent"] = "Ascent",
        ["/Game/Maps/Duality/Duality"] = "Bind",
        ["/Game/Maps/Triad/Triad"] = "Haven",
        ["/Game/Maps/Bonsai/Bonsai"] = "Split",
        ["/Game/Maps/Port/Port"] = "Icebox",
        ["/Game/Maps/Foxtrot/Foxtrot"] = "Breeze",
        ["/Game/Maps/Canyon/Canyon"] = "Fracture",
        ["/Game/Maps/Pitt/Pitt"] = "Pearl",
        ["/Game/Maps/Jam/Jam"] = "Lotus",
        ["/Game/Maps/Juliett/Juliett"] = "Sunset",
        ["/Game/Maps/Infinity/Infinity"] = "Abyss",
        ["/Game/Maps/Rook/Rook"] = "Corrode",
        ["/Game/Maps/HURM/HURM_Alley/HURM_Alley"] = "District",
        ["/Game/Maps/HURM/HURM_Bowl/HURM_Bowl"] = "Kasbah",
        ["/Game/Maps/HURM/HURM_Yard/HURM_Yard"] = "Piazza",
    };
}
