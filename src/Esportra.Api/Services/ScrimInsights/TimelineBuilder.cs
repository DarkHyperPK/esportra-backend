using System.Text.Json;

namespace Esportra.Api.Services.ScrimInsights;

/// <summary>A player's recorded position at a kill moment, facing in radians. Players are opaque per-match keys.</summary>
public sealed record TimelinePosition(string Key, int X, int Y, double? Facing);

public sealed record TimelineKill(
    int Ordinal, int? Ms, string? Killer, string Victim, string[] Assistants, string? Weapon, string? Type, int? VictimX, int? VictimY, IReadOnlyList<TimelinePosition> Positions);

public sealed record TimelineRound(
    int Num, bool? Won, string? MyRole, string? ResultCode, string? PlantSite, int? PlantMs, int? PlantX, int? PlantY, string? Planter, bool Defused, int? DefuseMs, int? DefuseX, int? DefuseY,
    string? Defuser, IReadOnlyList<TimelineKill> Kills);

public sealed record TimelinePlayer(string Key, string Name, string? Agent, bool Mine);

/// <summary>
/// One map round by round with every recorded position: who killed whom and when, where everyone stood at that moment, and where the spike went down
/// and came up. Positions exist at the moments Riot recorded (each kill, the plant, the defuse), not continuously.
/// </summary>
public sealed record MatchTimeline(IReadOnlyList<TimelinePlayer> Players, IReadOnlyList<TimelineRound> Rounds);

public static class TimelineBuilder
{
    /// <summary>The recorded player locations of a kill, with PUUIDs replaced by the match's player keys. Entries without a usable position are dropped.</summary>
    public static IReadOnlyList<TimelinePosition> Positions(string? json, IReadOnlyDictionary<string, string> keys)
    {
        if (string.IsNullOrEmpty(json)) return [];
        var list = new List<TimelinePosition>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                if (!p.TryGetProperty("puuid", out var id) || id.GetString() is not { } puuid || !keys.TryGetValue(puuid, out var key)) continue;
                if (!p.TryGetProperty("location", out var loc) || !loc.TryGetProperty("x", out var x) || !loc.TryGetProperty("y", out var y) || !x.TryGetInt32(out var xi) || !y.TryGetInt32(out var yi)) continue;
                if (Math.Abs(xi) < 1 && Math.Abs(yi) < 1) continue;
                double? facing = p.TryGetProperty("viewRadians", out var v) && v.TryGetDouble(out var f) ? Math.Round(f, 3) : null;
                list.Add(new TimelinePosition(key, xi, yi, facing));
            }
        }
        catch (JsonException) { }
        return list;
    }

    public static MatchTimeline Build(InMatch input, IReadOnlyDictionary<(int Round, int Ordinal), string?> locations)
    {
        var m = InsightsCommon.OneBased(input);
        var shift = input.Rounds.Count > 0 && input.Rounds.Min(r => r.Num) == 0 ? 1 : 0;
        var keys = InsightsCommon.Keys(m);
        string? Key(string? puuid) => puuid is not null && keys.TryGetValue(puuid, out var k) ? k : null;
        var players = m.Players.OrderBy(p => keys[p.Puuid], new OpeningMath.KeyOrder())
            .Select(p => new TimelinePlayer(keys[p.Puuid], p.Name ?? "Player", p.Agent?.ToLowerInvariant(), p.Side == m.MySide)).ToList();

        var rounds = m.Rounds.OrderBy(r => r.Num).Select(r =>
        {
            var kills = m.Kills.Where(k => k.Round == r.Num).OrderBy(k => k.RoundMs ?? int.MaxValue).ThenBy(k => k.Ordinal).Select(k =>
                new TimelineKill(k.Ordinal, k.RoundMs, Key(k.Killer), Key(k.Victim) ?? "?", k.Assistants.Select(a => Key(a) ?? "?").ToArray(), k.Item, k.Type, k.X, k.Y,
                    Positions(locations.GetValueOrDefault((k.Round - shift, k.Ordinal)), keys))).ToList();
            return new TimelineRound(r.Num, InsightsCommon.Won(m, r), InsightsCommon.MyRole(m, r), r.ResultCode, r.Planter is null ? null : r.Site, r.PlantMs, r.PlantX, r.PlantY, Key(r.Planter),
                r.Defuser is not null, r.DefuseMs, r.DefuseX, r.DefuseY, Key(r.Defuser), kills);
        }).ToList();
        return new MatchTimeline(players, rounds);
    }
}
