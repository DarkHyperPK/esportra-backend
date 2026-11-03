using System.Text.Json;
using Esportra.Api.Services.ScrimInsights;

namespace Esportra.Api.Services.ScrimReports;

/// <summary>One recorded event position, in Riot's own coordinates, and which side the team was on in that round.</summary>
public sealed record HeatPoint(int X, int Y, int Round, string? Side);

/// <summary>
/// Where the team's recorded events happened: where its kills were made from, where its players died, opening kills and deaths, plants and defuses.
/// These are positions at event moments Riot recorded, never movement. Coordinates are Riot's; the page turns them into map positions.
/// </summary>
public static class HeatmapMath
{
    public static readonly string[] Kinds = ["kills", "deaths", "opening_kills", "opening_deaths", "plants", "defuses"];
    public const int PointCap = 3000;

    /// <summary>Where a player stood when a kill was recorded, from the kill's recorded player locations. Null when Riot gave no location for them.</summary>
    public static (int X, int Y)? LocationOf(string? playerLocationsJson, string? puuid)
    {
        if (string.IsNullOrEmpty(playerLocationsJson) || puuid is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(playerLocationsJson);
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                if (!p.TryGetProperty("puuid", out var id) || id.GetString() != puuid) continue;
                if (p.TryGetProperty("location", out var loc) && loc.TryGetProperty("x", out var x) && loc.TryGetProperty("y", out var y) && x.TryGetInt32(out var xi) && y.TryGetInt32(out var yi)) return (xi, yi);
            }
        }
        catch (JsonException) { }
        return null;
    }

    public static IReadOnlyList<HeatPoint> Points(InMatch input, IReadOnlyDictionary<(int Round, int Ordinal), string?> locations, string kind, string? side)
    {
        var m = InsightsCommon.OneBased(input);
        string? SideOf(int round) => InsightsCommon.MyRole(m, m.Rounds.First(r => r.Num == round));
        bool Mine(string? puuid) => InsightsCommon.SideOf(m, puuid) == m.MySide;
        var points = new List<HeatPoint>();
        void Add(int? x, int? y, int round)
        {
            if (x is null || y is null || (Math.Abs(x.Value) < 1 && Math.Abs(y.Value) < 1)) return;
            var s = m.Rounds.Any(r => r.Num == round) ? SideOf(round) : null;
            if (side is not null && s != side) return;
            points.Add(new HeatPoint(x.Value, y.Value, round, s));
        }

        // Locations are keyed by the match's own round numbers; shift them the same way the rounds were.
        var shift = input.Rounds.Count > 0 && input.Rounds.Min(r => r.Num) == 0 ? 1 : 0;
        switch (kind)
        {
            case "kills" or "opening_kills":
                var kills = kind == "kills" ? m.Kills.Where(k => Mine(k.Killer)) : m.Rounds.Select(r => OpeningMath.FirstBlood(m.Kills.Where(k => k.Round == r.Num))).Where(k => k is not null && Mine(k.Killer)).Select(k => k!);
                foreach (var k in kills)
                    if (locations.TryGetValue((k.Round - shift, k.Ordinal), out var json) && LocationOf(json, k.Killer) is { } at) Add(at.X, at.Y, k.Round);
                break;
            case "deaths" or "opening_deaths":
                var deaths = kind == "deaths" ? m.Kills.Where(k => Mine(k.Victim)) : m.Rounds.Select(r => OpeningMath.FirstBlood(m.Kills.Where(k => k.Round == r.Num))).Where(k => k is not null && Mine(k.Victim)).Select(k => k!);
                foreach (var k in deaths) Add(k.X, k.Y, k.Round);
                break;
            case "plants":
                foreach (var r in m.Rounds.Where(r => r.Planter is not null && Mine(r.Planter))) Add(r.PlantX, r.PlantY, r.Num);
                break;
            case "defuses":
                foreach (var r in m.Rounds.Where(r => r.Defuser is not null && Mine(r.Defuser))) Add(r.DefuseX, r.DefuseY, r.Num);
                break;
        }
        return points;
    }
}
