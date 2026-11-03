using System.Text.Json;
using Esportra.Api.Services.ScrimInsights;

namespace Esportra.Api.Services.ScrimReports;

/// <summary>
/// One kill seen from the team's side: where it happened (the victim's position, always recorded), whether the team won the fight,
/// and for the team's own deaths how far the closest teammate was and whether the death was traded.
/// </summary>
public sealed record Fight(int X, int Y, int Round, string? Side, bool Won, int? NearestAlly, bool? Traded);

/// <summary>
/// Where the team wins and loses fights, and how spread out it was when it lost them. Positions are Riot's coordinates at kill moments only.
/// The distance is in Riot's units (100 units = 1 metre on the map); a teammate with no recorded position is not counted.
/// </summary>
public static class FightsMath
{
    public const int Cap = 3000;

    public static IReadOnlyList<Fight> Build(InMatch input, IReadOnlyDictionary<(int Round, int Ordinal), string?> locations, string? side)
    {
        var m = InsightsCommon.OneBased(input);
        var shift = input.Rounds.Count > 0 && input.Rounds.Min(r => r.Num) == 0 ? 1 : 0;
        var fights = new List<Fight>();
        foreach (var k in m.Kills)
        {
            if (k.X is not { } x || k.Y is not { } y || (Math.Abs(x) < 1 && Math.Abs(y) < 1)) continue;
            var round = m.Rounds.FirstOrDefault(r => r.Num == k.Round);
            var role = round is null ? null : InsightsCommon.MyRole(m, round);
            if (side is not null && role != side) continue;
            var mineKilled = InsightsCommon.SideOf(m, k.Killer) == m.MySide;
            var mineDied = InsightsCommon.SideOf(m, k.Victim) == m.MySide;
            if (mineKilled == mineDied) continue;
            locations.TryGetValue((k.Round - shift, k.Ordinal), out var json);
            fights.Add(new Fight(x, y, k.Round, role, mineKilled, mineDied ? NearestAlly(m, k, json) : null, mineDied ? RoundStates.IsTraded(m, k) : null));
        }
        return fights;
    }

    private static int? NearestAlly(InMatch m, InKill death, string? json)
    {
        if (string.IsNullOrEmpty(json) || death.X is not { } vx || death.Y is not { } vy) return null;
        double? best = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                var puuid = p.TryGetProperty("puuid", out var id) ? id.GetString() : null;
                if (puuid is null || puuid == death.Victim || InsightsCommon.SideOf(m, puuid) != m.MySide) continue;
                if (!p.TryGetProperty("location", out var loc) || !loc.TryGetProperty("x", out var lx) || !loc.TryGetProperty("y", out var ly)) continue;
                if (!lx.TryGetInt32(out var ax) || !ly.TryGetInt32(out var ay)) continue;
                var d = Math.Sqrt(Math.Pow(ax - vx, 2) + Math.Pow(ay - vy, 2));
                if (best is null || d < best) best = d;
            }
        }
        catch (JsonException) { return null; }
        return best is null ? null : (int)Math.Round(best.Value);
    }
}
