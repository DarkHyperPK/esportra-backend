using System.Text.Json;
using Esportra.Api.Services;

namespace Esportra.Api.ScoreboardOcr;

/// <summary>Where a Riot lookup for a screenshot ended up: found, not found, unavailable (the key can't
/// read Valorant matches) or skipped (no score to search by, or no linked player to search from).</summary>
public static class RiotLookupState
{
    public const string Found = "found";
    public const string NotFound = "not_found";
    public const string Unavailable = "unavailable";
    public const string Skipped = "skipped";
}

public sealed record RiotLookupOutcome(string Status, RiotMatch? Match);

/// <summary>
/// Finds the game a scoreboard screenshot shows in Riot's match history and shapes it for the review:
/// the same per-player payload "Auto report" submits, with each player's full Riot ID and bracket team.
/// </summary>
public static class ScoreboardRiotMatcher
{
    public static readonly TimeSpan MatchWindow = TimeSpan.FromDays(2);

    /// <summary>Map, score and date from the OCR read; null when the score couldn't be read.</summary>
    public static RiotMatchQuery? QueryFrom(JsonElement result, DateTimeOffset fallback, TimeSpan window)
    {
        if (Int(result, "allyScore") is not { } ally || Int(result, "enemyScore") is not { } enemy) return null;
        var map = result.TryGetProperty("map", out var m) && m.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object
            && v.TryGetProperty("name", out var name) ? name.GetString() : null;
        var playedOn = result.TryGetProperty("playedOn", out var p) && p.TryGetProperty("value", out var pv) && pv.ValueKind == JsonValueKind.String
            && DateTime.TryParse(pv.GetString(), out var date)
            ? new DateTimeOffset(date.Date.AddHours(12), TimeSpan.Zero)
            : fallback;
        return new RiotMatchQuery(map, ally, enemy, playedOn, window);
    }

    /// <summary>Bounded: Riot being slow or down never holds up an upload.</summary>
    public static async Task<RiotLookupOutcome> FindAsync(
        IRiotMatchLookup lookup, IReadOnlyList<string> puuids, RiotMatchQuery? query, TimeSpan budget, CancellationToken ct)
    {
        if (query is null || puuids.Count == 0) return new RiotLookupOutcome(RiotLookupState.Skipped, null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);
        try
        {
            var found = await lookup.FindCustomGameAsync(puuids, query, timeout.Token);
            return found.Status switch
            {
                RiotLookupStatus.Found when found.Match is not null => new RiotLookupOutcome(RiotLookupState.Found, found.Match),
                RiotLookupStatus.Unavailable => new RiotLookupOutcome(RiotLookupState.Unavailable, null),
                _ => new RiotLookupOutcome(RiotLookupState.NotFound, null),
            };
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException && !ct.IsCancellationRequested)
        {
            return new RiotLookupOutcome(RiotLookupState.NotFound, null);
        }
    }

    /// <summary>The bracket team (team1/team2) each Riot side ("Blue"/"Red") belongs to, from linked accounts.</summary>
    public static (string? Blue, string? Red) SideTeams(RiotMatch match, IReadOnlyList<LinkedAccount> linked)
    {
        var owners = linked.GroupBy(a => a.Puuid).ToDictionary(g => g.Key, g => g.First().Team);
        string? Team(string side) => match.Players.Where(p => p.TeamId == side && owners.ContainsKey(p.Puuid))
            .GroupBy(p => owners[p.Puuid]).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var (blue, red) = (Team("Blue"), Team("Red"));
        // One side known is enough: the other side is the other team.
        blue ??= red is null ? null : Other(red);
        red ??= blue is null ? null : Other(blue);
        return blue == red ? (null, null) : (blue, red);
    }

    /// <summary>The review payload: rounds per bracket team and Riot's players with full Riot IDs.</summary>
    public static object Payload(RiotMatch match, IReadOnlyList<LinkedAccount> linked)
    {
        var (blue, _) = SideTeams(match, linked);
        var team1 = linked.Where(a => a.Team == "team1").Select(a => a.Puuid).ToHashSet();
        var team2 = linked.Where(a => a.Team == "team2").Select(a => a.Puuid).ToHashSet();
        if (blue is not null)
        {
            // Teammates who aren't linked still belong to their side's bracket team.
            foreach (var player in match.Players)
                (player.TeamId == "Blue" == (blue == "team1") ? team1 : team2).Add(player.Puuid);
        }
        var players = new List<object>();
        if (match.Raw is { } raw)
        {
            var rounds = ValorantMatchStatsHelper.BuildRoundAggregates(raw);
            players.AddRange(raw.GetProperty("players").EnumerateArray().Select(p => ValorantMatchStatsHelper.BuildPlayerPayload(p, rounds, team1, team2)));
        }
        int? team1Rounds = blue is null ? null : blue == "team1" ? match.BlueRounds : match.RedRounds;
        int? team2Rounds = blue is null ? null : blue == "team1" ? match.RedRounds : match.BlueRounds;
        return new { matchId = match.MatchId, map = match.Map, startedAt = match.StartedAt, team1Rounds, team2Rounds, players };
    }

    /// <summary>The score as team1/team2 from the OCR read (ally side → bracket team), when it's known.</summary>
    public static (int Team1, int Team2)? ScoreFrom(JsonElement result)
    {
        if (Int(result, "allyScore") is not { } ally || Int(result, "enemyScore") is not { } enemy) return null;
        var allyTeam = result.TryGetProperty("allyTeam", out var a) && a.TryGetProperty("value", out var v) ? v.GetString() : null;
        return allyTeam switch
        {
            "team1" => (ally, enemy),
            "team2" => (enemy, ally),
            _ => null,
        };
    }

    private static string Other(string team) => team == "team1" ? "team2" : "team1";

    private static int? Int(JsonElement result, string field) =>
        result.TryGetProperty(field, out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty("value", out var v) && v.TryGetInt32(out var n) ? n : null;
}
