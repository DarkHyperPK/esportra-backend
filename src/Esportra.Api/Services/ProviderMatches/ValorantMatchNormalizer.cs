using System.Text.Json;

namespace Esportra.Api.Services.ProviderMatches;

/// <summary>
/// Turns a VAL-MATCH-V1 response into normalized rows. Pure: the same payload always yields the same rows, so
/// ingestion is repeatable. Formulas and tie-breaks are documented in docs/competitive-intelligence/03.
/// </summary>
public static class ValorantMatchNormalizer
{
    /// <summary>Bump when a derived column's meaning changes so stored matches can be re-derived from the payload.</summary>
    public const int ParserVersion = 1;

    public static NormalizedValorantMatch? Normalize(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("matchInfo", out var info) ||
            Str(info, "matchId") is not { Length: > 0 } matchId) return null;

        var (rounds, roundStats, damage, kills) = ReadRounds(root);
        var players = ReadPlayers(root, damage, kills, rounds);
        return new NormalizedValorantMatch(
            ReadInfo(info, matchId), ReadTeams(root), players, rounds, roundStats, damage, kills);
    }

    private static NormalizedMatchInfo ReadInfo(JsonElement info, string matchId)
    {
        var queue = Str(info, "queueId");
        var customName = Str(info, "customGameName");
        var isCustom = string.Equals(Str(info, "provisioningFlowId"), "CustomGame", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(queue, "custom", StringComparison.OrdinalIgnoreCase)
                       || (string.IsNullOrEmpty(queue) && !string.IsNullOrEmpty(customName));
        var started = Long(info, "gameStartMillis");
        return new NormalizedMatchInfo(
            matchId, Str(info, "mapId"), queue, isCustom, Str(info, "gameMode"),
            started is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
            Int(info, "gameLengthMillis"), Str(info, "gameVersion"), Str(info, "seasonId"),
            Bool(info, "isCompleted") ?? false, Str(info, "region"));
    }

    private static List<NormalizedTeam> ReadTeams(JsonElement root) =>
        Array(root, "teams")
            .Where(t => Str(t, "teamId") is { Length: > 0 })
            .Select(t => new NormalizedTeam(Str(t, "teamId")!, Bool(t, "won"), Int(t, "roundsPlayed"), Int(t, "roundsWon"), Int(t, "numPoints")))
            .OrderBy(t => t.TeamId, StringComparer.Ordinal)
            .ToList();

    private static (List<NormalizedRound>, List<NormalizedRoundPlayerStat>, List<NormalizedDamage>, List<NormalizedKill>) ReadRounds(JsonElement root)
    {
        var rounds = new List<NormalizedRound>();
        var stats = new List<NormalizedRoundPlayerStat>();
        var damage = new List<NormalizedDamage>();
        var kills = new List<NormalizedKill>();

        foreach (var r in Array(root, "roundResults"))
        {
            var num = Int(r, "roundNum") ?? rounds.Count;
            var plant = Obj(r, "plantLocation");
            var defuse = Obj(r, "defuseLocation");
            rounds.Add(new NormalizedRound(
                num, Str(r, "roundResult"), Str(r, "roundResultCode"), Str(r, "roundCeremony"), Str(r, "winningTeam"),
                Str(r, "winningTeamRole"), NullIfEmpty(Str(r, "bombPlanter")), NullIfEmpty(Str(r, "bombDefuser")),
                Int(r, "plantRoundTime"), NullIfEmpty(Str(r, "plantSite")),
                plant is { } p ? Int(p, "x") : null, plant is { } p2 ? Int(p2, "y") : null,
                Int(r, "defuseRoundTime"),
                defuse is { } d ? Int(d, "x") : null, defuse is { } d2 ? Int(d2, "y") : null));

            var roundKills = new List<NormalizedKill>();
            foreach (var ps in Array(r, "playerStats"))
            {
                if (Str(ps, "puuid") is not { Length: > 0 } puuid) continue;
                var econ = Obj(ps, "economy");
                var perPlayerKills = Array(ps, "kills").ToList();
                stats.Add(new NormalizedRoundPlayerStat(
                    num, puuid, perPlayerKills.Count, Int(ps, "score") ?? 0,
                    econ is { } e ? Int(e, "loadoutValue") : null, econ is { } e1 ? NullIfEmpty(Str(e1, "weapon")) : null,
                    econ is { } e2 ? NullIfEmpty(Str(e2, "armor")) : null, econ is { } e3 ? Int(e3, "spent") : null,
                    econ is { } e4 ? Int(e4, "remaining") : null,
                    Obj(ps, "ability") is { } ab ? ab.GetRawText() : null));
                damage.AddRange(Array(ps, "damage")
                    .Where(d => Str(d, "receiver") is { Length: > 0 })
                    .Select(d => new NormalizedDamage(
                        num, puuid, Str(d, "receiver")!, Int(d, "damage") ?? 0,
                        Int(d, "headshots") ?? 0, Int(d, "bodyshots") ?? 0, Int(d, "legshots") ?? 0)));
                roundKills.AddRange(perPlayerKills.Select(k => ReadKill(num, k)));
            }

            // Kills are nested under their killer, so source order is not chronological: order by time, then a fixed key.
            kills.AddRange(roundKills
                .OrderBy(k => k.TimeSinceRoundMs ?? int.MaxValue)
                .ThenBy(k => k.TimeSinceGameMs ?? int.MaxValue)
                .ThenBy(k => k.Killer ?? "", StringComparer.Ordinal)
                .ThenBy(k => k.Victim, StringComparer.Ordinal)
                .Select((k, i) => k with { Ordinal = i + 1 }));
        }

        // Keys must be unique per match: repeats (not expected from Riot) are merged rather than failing the whole ingest.
        var mergedDamage = damage
            .GroupBy(d => (d.RoundNum, d.Puuid, d.Receiver))
            .Select(g => new NormalizedDamage(
                g.Key.RoundNum, g.Key.Puuid, g.Key.Receiver, g.Sum(x => x.Damage),
                g.Sum(x => x.Headshots), g.Sum(x => x.Bodyshots), g.Sum(x => x.Legshots)))
            .OrderBy(d => d.RoundNum).ThenBy(d => d.Puuid, StringComparer.Ordinal).ThenBy(d => d.Receiver, StringComparer.Ordinal)
            .ToList();
        return (rounds.DistinctBy(r => r.RoundNum).OrderBy(r => r.RoundNum).ToList(),
                stats.DistinctBy(s => (s.RoundNum, s.Puuid)).OrderBy(s => s.RoundNum).ThenBy(s => s.Puuid, StringComparer.Ordinal).ToList(),
                mergedDamage,
                kills);
    }

    private static NormalizedKill ReadKill(int round, JsonElement k)
    {
        var victimLoc = Obj(k, "victimLocation");
        var fin = Obj(k, "finishingDamage");
        return new NormalizedKill(
            round, 0, NullIfEmpty(Str(k, "killer")), Str(k, "victim") ?? "",
            Array(k, "assistants").Select(a => a.GetString() ?? "").Where(a => a.Length > 0).ToArray(),
            Int(k, "timeSinceGameStartMillis"), Int(k, "timeSinceRoundStartMillis"),
            victimLoc is { } v ? Int(v, "x") : null, victimLoc is { } v2 ? Int(v2, "y") : null,
            fin is { } f ? NullIfEmpty(Str(f, "damageType")) : null, fin is { } f2 ? NullIfEmpty(Str(f2, "damageItem")) : null,
            fin is { } f3 ? Bool(f3, "isSecondaryFireMode") : null,
            k.TryGetProperty("playerLocations", out var pl) && pl.ValueKind == JsonValueKind.Array ? pl.GetRawText() : null);
    }

    private static List<NormalizedPlayer> ReadPlayers(
        JsonElement root, List<NormalizedDamage> damage, List<NormalizedKill> kills, List<NormalizedRound> rounds)
    {
        var dealt = damage.GroupBy(d => d.Puuid).ToDictionary(g => g.Key, g => g.ToList());
        var firstKills = kills.Where(k => k.Ordinal == 1 && k.Killer is not null).GroupBy(k => k.Killer!).ToDictionary(g => g.Key, g => g.Count());
        var firstDeaths = kills.Where(k => k.Ordinal == 1).GroupBy(k => k.Victim).ToDictionary(g => g.Key, g => g.Count());
        // Multi-kill: kills by one player inside one round only.
        var multi = kills.Where(k => k.Killer is not null)
            .GroupBy(k => (k.RoundNum, Killer: k.Killer!))
            .GroupBy(g => g.Key.Killer, g => g.Count())
            .ToDictionary(g => g.Key, g => g.ToList());

        return Array(root, "players")
            .Where(p => Str(p, "puuid") is { Length: > 0 } && Str(p, "teamId") is { Length: > 0 })
            .Select(p =>
            {
                var puuid = Str(p, "puuid")!;
                var stats = Obj(p, "stats");
                var casts = stats is { } s ? Obj(s, "abilityCasts") : null;
                var hits = dealt.GetValueOrDefault(puuid) ?? [];
                var counts = multi.GetValueOrDefault(puuid) ?? [];
                return new NormalizedPlayer(
                    puuid, Str(p, "teamId")!, Str(p, "gameName"), Str(p, "tagLine"), Str(p, "partyId"), Str(p, "characterId"),
                    Int(p, "competitiveTier"), Bool(p, "isObserver") ?? false,
                    stats is { } s1 ? Int(s1, "score") ?? 0 : 0, stats is { } s2 ? Int(s2, "roundsPlayed") ?? 0 : 0,
                    stats is { } s3 ? Int(s3, "kills") ?? 0 : 0, stats is { } s4 ? Int(s4, "deaths") ?? 0 : 0,
                    stats is { } s5 ? Int(s5, "assists") ?? 0 : 0, stats is { } s6 ? Int(s6, "playtimeMillis") : null,
                    casts is { } c ? Int(c, "grenadeCasts") : null, casts is { } c1 ? Int(c1, "ability1Casts") : null,
                    casts is { } c2 ? Int(c2, "ability2Casts") : null, casts is { } c3 ? Int(c3, "ultimateCasts") : null,
                    hits.Sum(h => h.Damage), hits.Sum(h => h.Headshots), hits.Sum(h => h.Bodyshots), hits.Sum(h => h.Legshots),
                    firstKills.GetValueOrDefault(puuid), firstDeaths.GetValueOrDefault(puuid),
                    counts.Count(c => c == 2), counts.Count(c => c == 3), counts.Count(c => c == 4), counts.Count(c => c >= 5),
                    rounds.Count(r => r.BombPlanter == puuid), rounds.Count(r => r.BombDefuser == puuid));
            })
            .DistinctBy(p => p.Puuid)
            .OrderBy(p => p.RiotSide, StringComparer.Ordinal).ThenBy(p => p.Puuid, StringComparer.Ordinal)
            .ToList();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : [];

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
