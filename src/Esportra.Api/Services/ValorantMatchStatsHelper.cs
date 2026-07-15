using System.Text.Json;

namespace Esportra.Api.Services;

internal static class ValorantMatchStatsHelper
{
    internal sealed record RoundAggregates(
        int TotalDamage,
        int Headshots,
        int Bodyshots,
        int Legshots,
        int FirstBloods);

    internal sealed record RoundTimelineEntry(
        int Round,
        string WinningTeam,
        string? ResultCode,
        string? Result,
        string? PlantSite);

    internal sealed record EconomyTimelineEntry(
        int Round,
        int BlueSpent,
        int RedSpent,
        int BlueLoadout,
        int RedLoadout);

    internal sealed record WeaponSummaryEntry(string Weapon, int RoundCount);

    internal sealed record DerivedMatchDetails(
        IReadOnlyList<RoundTimelineEntry> RoundTimeline,
        IReadOnlyList<EconomyTimelineEntry> EconomyTimeline,
        IReadOnlyList<WeaponSummaryEntry> WeaponSummaries);

    internal static Dictionary<string, RoundAggregates> BuildRoundAggregates(JsonElement root)
    {
        var aggregates = new Dictionary<string, RoundAggregates>(StringComparer.Ordinal);

        if (!root.TryGetProperty("roundResults", out var roundResults)
            || roundResults.ValueKind != JsonValueKind.Array)
        {
            return aggregates;
        }

        foreach (var round in roundResults.EnumerateArray())
        {
            if (!round.TryGetProperty("playerStats", out var playerStats)
                || playerStats.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var roundKills = new List<(string Killer, long TimeMs)>();

            foreach (var roundPlayer in playerStats.EnumerateArray())
            {
                var roundPlayerPuuid = roundPlayer.TryGetProperty("puuid", out var puuidEl)
                    ? puuidEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(roundPlayerPuuid)) continue;

                if (!aggregates.TryGetValue(roundPlayerPuuid, out var current))
                {
                    current = new RoundAggregates(0, 0, 0, 0, 0);
                }

                var roundDamage = 0;
                var roundHeadshots = 0;
                var roundBodyshots = 0;
                var roundLegshots = 0;

                if (roundPlayer.TryGetProperty("damage", out var damageEntries)
                    && damageEntries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var damageEntry in damageEntries.EnumerateArray())
                    {
                        if (damageEntry.TryGetProperty("damage", out var damageValue)
                            && damageValue.TryGetInt32(out var damageAmount))
                        {
                            roundDamage += damageAmount;
                        }

                        roundHeadshots += ReadIntProperty(damageEntry, "headshots");
                        roundBodyshots += ReadIntProperty(damageEntry, "bodyshots");
                        roundLegshots += ReadIntProperty(damageEntry, "legshots");
                    }
                }

                aggregates[roundPlayerPuuid] = current with
                {
                    TotalDamage = current.TotalDamage + roundDamage,
                    Headshots = current.Headshots + roundHeadshots,
                    Bodyshots = current.Bodyshots + roundBodyshots,
                    Legshots = current.Legshots + roundLegshots,
                };

                if (roundPlayer.TryGetProperty("kills", out var kills)
                    && kills.ValueKind == JsonValueKind.Array)
                {
                    foreach (var kill in kills.EnumerateArray())
                    {
                        if (!kill.TryGetProperty("timeSinceRoundStartMillis", out var timeEl)
                            || !timeEl.TryGetInt64(out var killTime))
                        {
                            continue;
                        }

                        var killer = kill.TryGetProperty("killer", out var killerEl)
                            ? killerEl.GetString()
                            : null;
                        if (string.IsNullOrWhiteSpace(killer))
                        {
                            killer = roundPlayerPuuid;
                        }

                        roundKills.Add((killer, killTime));
                    }
                }
            }

            if (roundKills.Count == 0) continue;

            var firstBloodKiller = roundKills
                .OrderBy(entry => entry.TimeMs)
                .First()
                .Killer;

            if (!aggregates.TryGetValue(firstBloodKiller, out var fbCurrent))
            {
                fbCurrent = new RoundAggregates(0, 0, 0, 0, 0);
            }

            aggregates[firstBloodKiller] = fbCurrent with
            {
                FirstBloods = fbCurrent.FirstBloods + 1,
            };
        }

        return aggregates;
    }

    internal static object BuildPlayerPayload(
        JsonElement player,
        IReadOnlyDictionary<string, RoundAggregates> roundAggregates,
        HashSet<string> team1Puuids,
        HashSet<string> team2Puuids)
    {
        var pPuuid = player.GetProperty("puuid").GetString() ?? "";
        var pTeam = player.GetProperty("teamId").GetString() ?? "";
        var pName = player.TryGetProperty("gameName", out var gn) ? gn.GetString() ?? "" : "";
        var pTag = player.TryGetProperty("tagLine", out var tl) ? tl.GetString() ?? "" : "";
        var charId = player.GetProperty("characterId").GetString() ?? "";
        var stats = player.GetProperty("stats");
        var pK = stats.GetProperty("kills").GetInt32();
        var pD = stats.GetProperty("deaths").GetInt32();
        var pA = stats.GetProperty("assists").GetInt32();
        var pScore = stats.GetProperty("score").GetInt32();
        var roundsPlayed = stats.TryGetProperty("roundsPlayed", out var roundsPlayedEl)
            && roundsPlayedEl.TryGetInt32(out var parsedRoundsPlayed)
            ? parsedRoundsPlayed
            : 0;

        int? adr = null;
        double? hsPct = null;
        int? firstBloods = null;
        if (roundAggregates.TryGetValue(pPuuid, out var roundStats))
        {
            adr = ComputeAdr(roundStats.TotalDamage, roundsPlayed);
            hsPct = ComputeHeadshotPercent(
                roundStats.Headshots,
                roundStats.Bodyshots,
                roundStats.Legshots);
            firstBloods = roundStats.FirstBloods;
        }

        return new
        {
            puuid = pPuuid,
            gameName = pName,
            tagLine = pTag,
            teamId = pTeam,
            characterId = charId,
            kills = pK,
            deaths = pD,
            assists = pA,
            score = pScore,
            roundsPlayed = roundsPlayed > 0 ? roundsPlayed : (int?)null,
            acs = ComputeAcs(pScore, roundsPlayed),
            adr,
            hsPct,
            kdRatio = ComputeKdRatio(pK, pD),
            firstBloods,
            abilityCasts = ParseAbilityCasts(stats),
            isTeam1 = team1Puuids.Contains(pPuuid),
            isTeam2 = team2Puuids.Contains(pPuuid),
        };
    }

    internal static object? BuildMatchInfoPayload(JsonElement root)
    {
        if (!root.TryGetProperty("matchInfo", out var info))
        {
            return null;
        }

        return new
        {
            matchId = info.TryGetProperty("matchId", out var matchIdEl) ? matchIdEl.GetString() : null,
            mapId = info.TryGetProperty("mapId", out var mapIdEl) ? mapIdEl.GetString() : null,
            gameVersion = info.TryGetProperty("gameVersion", out var versionEl) ? versionEl.GetString() : null,
            gameLengthMillis = info.TryGetProperty("gameLengthMillis", out var lengthEl) && lengthEl.TryGetInt64(out var length)
                ? length
                : 0L,
            region = info.TryGetProperty("region", out var regionEl) ? regionEl.GetString() : null,
            gameStartMillis = info.TryGetProperty("gameStartMillis", out var startEl) && startEl.TryGetInt64(out var start)
                ? start
                : 0L,
            queueId = info.TryGetProperty("queueId", out var queueEl) ? queueEl.GetString() : null,
            gameMode = info.TryGetProperty("gameMode", out var modeEl) ? modeEl.GetString() : null,
            isRanked = info.TryGetProperty("isRanked", out var rankedEl) && rankedEl.ValueKind == JsonValueKind.True,
            isCompleted = info.TryGetProperty("isCompleted", out var completedEl) && completedEl.ValueKind == JsonValueKind.True,
        };
    }

    internal static int? ComputeAcs(int score, int roundsPlayed) =>
        roundsPlayed > 0 ? (int)Math.Round((double)score / roundsPlayed) : null;

    internal static double? ComputeKdRatio(int kills, int deaths) =>
        deaths > 0
            ? Math.Round(kills / (double)deaths, 2)
            : kills > 0 ? (double)kills : null;

    internal static int? ComputeAdr(int totalDamage, int roundsPlayed) =>
        roundsPlayed > 0 ? (int)Math.Round((double)totalDamage / roundsPlayed) : null;

    internal static double? ComputeHeadshotPercent(int headshots, int bodyshots, int legshots)
    {
        var totalShots = headshots + bodyshots + legshots;
        return totalShots > 0
            ? Math.Round(headshots * 100d / totalShots, 1)
            : null;
    }

    internal static DerivedMatchDetails BuildDerivedMatchDetails(JsonElement root)
    {
        return new DerivedMatchDetails(
            BuildRoundTimeline(root),
            BuildEconomyTimeline(root),
            BuildWeaponSummaries(root));
    }

    internal sealed record SerializedDerivedDetails(
        IReadOnlyList<object> RoundTimeline,
        IReadOnlyList<object> EconomyTimeline,
        IReadOnlyList<object> WeaponSummaries);

    internal static SerializedDerivedDetails SerializeDerivedDetails(DerivedMatchDetails derived) => new(
        derived.RoundTimeline.Select(round => (object)new
        {
            round = round.Round,
            winningTeam = round.WinningTeam,
            resultCode = round.ResultCode,
            result = round.Result,
            plantSite = round.PlantSite,
        }).ToList(),
        derived.EconomyTimeline.Select(entry => (object)new
        {
            round = entry.Round,
            blueSpent = entry.BlueSpent,
            redSpent = entry.RedSpent,
            blueLoadout = entry.BlueLoadout,
            redLoadout = entry.RedLoadout,
        }).ToList(),
        derived.WeaponSummaries.Select(entry => (object)new
        {
            weapon = entry.Weapon,
            roundCount = entry.RoundCount,
        }).ToList());

    internal static List<RoundTimelineEntry> BuildRoundTimeline(JsonElement root)
    {
        var timeline = new List<RoundTimelineEntry>();

        if (!root.TryGetProperty("roundResults", out var roundResults)
            || roundResults.ValueKind != JsonValueKind.Array)
        {
            return timeline;
        }

        var fallbackRound = 0;
        foreach (var round in roundResults.EnumerateArray())
        {
            fallbackRound++;
            var roundNumber = round.TryGetProperty("roundNum", out var roundNumEl)
                && roundNumEl.TryGetInt32(out var roundNum)
                && roundNum > 0
                ? roundNum
                : fallbackRound;

            var winningTeam = round.TryGetProperty("winningTeam", out var winningTeamEl)
                ? winningTeamEl.GetString() ?? string.Empty
                : string.Empty;
            var resultCode = ResolveRoundResultCode(round);
            var roundResult = round.TryGetProperty("roundResult", out var roundResultEl)
                ? roundResultEl.GetString()
                : null;
            var plantSite = round.TryGetProperty("plantSite", out var plantSiteEl)
                ? plantSiteEl.GetString()
                : null;

            timeline.Add(new RoundTimelineEntry(roundNumber, winningTeam, resultCode, roundResult, plantSite));
        }

        return timeline;
    }

    internal static List<EconomyTimelineEntry> BuildEconomyTimeline(JsonElement root)
    {
        var timeline = new List<EconomyTimelineEntry>();
        var teamByPuuid = BuildTeamByPuuid(root);

        if (!root.TryGetProperty("roundResults", out var roundResults)
            || roundResults.ValueKind != JsonValueKind.Array)
        {
            return timeline;
        }

        var fallbackRound = 0;
        foreach (var round in roundResults.EnumerateArray())
        {
            fallbackRound++;
            var roundNumber = round.TryGetProperty("roundNum", out var roundNumEl)
                && roundNumEl.TryGetInt32(out var roundNum)
                && roundNum > 0
                ? roundNum
                : fallbackRound;

            var blueSpent = 0;
            var redSpent = 0;
            var blueLoadout = 0;
            var redLoadout = 0;

            if (round.TryGetProperty("playerStats", out var playerStats)
                && playerStats.ValueKind == JsonValueKind.Array)
            {
                foreach (var roundPlayer in playerStats.EnumerateArray())
                {
                    var puuid = roundPlayer.TryGetProperty("puuid", out var puuidEl)
                        ? puuidEl.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(puuid)
                        || !teamByPuuid.TryGetValue(puuid, out var teamId))
                    {
                        continue;
                    }

                    if (!roundPlayer.TryGetProperty("economy", out var economyEl))
                    {
                        continue;
                    }

                    var spent = economyEl.TryGetProperty("spent", out var spentEl)
                        && spentEl.TryGetInt32(out var parsedSpent)
                        ? parsedSpent
                        : 0;
                    var loadout = economyEl.TryGetProperty("loadoutValue", out var loadoutEl)
                        && loadoutEl.TryGetInt32(out var parsedLoadout)
                        ? parsedLoadout
                        : 0;

                    if (string.Equals(teamId, "Blue", StringComparison.OrdinalIgnoreCase))
                    {
                        blueSpent += spent;
                        blueLoadout += loadout;
                    }
                    else if (string.Equals(teamId, "Red", StringComparison.OrdinalIgnoreCase))
                    {
                        redSpent += spent;
                        redLoadout += loadout;
                    }
                }
            }

            timeline.Add(new EconomyTimelineEntry(roundNumber, blueSpent, redSpent, blueLoadout, redLoadout));
        }

        return timeline;
    }

    internal static List<WeaponSummaryEntry> BuildWeaponSummaries(JsonElement root)
    {
        var weaponCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetProperty("roundResults", out var roundResults)
            || roundResults.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var round in roundResults.EnumerateArray())
        {
            if (!round.TryGetProperty("playerStats", out var playerStats)
                || playerStats.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var roundPlayer in playerStats.EnumerateArray())
            {
                if (!roundPlayer.TryGetProperty("economy", out var economyEl)
                    || !economyEl.TryGetProperty("weapon", out var weaponEl))
                {
                    continue;
                }

                var weapon = weaponEl.GetString();
                if (string.IsNullOrWhiteSpace(weapon)) continue;

                weaponCounts[weapon] = weaponCounts.GetValueOrDefault(weapon) + 1;
            }
        }

        return weaponCounts
            .OrderByDescending(entry => entry.Value)
            .Select(entry => new WeaponSummaryEntry(entry.Key, entry.Value))
            .ToList();
    }

    private static string? ResolveRoundResultCode(JsonElement round)
    {
        if (round.TryGetProperty("roundResultCode", out var resultCodeEl))
        {
            var code = resultCodeEl.GetString();
            if (!string.IsNullOrWhiteSpace(code)) return code;
        }

        if (round.TryGetProperty("roundResult", out var roundResultEl))
        {
            return roundResultEl.GetString();
        }

        return null;
    }

    private static object? ParseAbilityCasts(JsonElement stats)
    {
        if (!stats.TryGetProperty("abilityCasts", out var casts)
            || casts.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new
        {
            grenadeCasts = ReadIntProperty(casts, "grenadeCasts"),
            ability1Casts = ReadIntProperty(casts, "ability1Casts"),
            ability2Casts = ReadIntProperty(casts, "ability2Casts"),
            ultimateCasts = ReadIntProperty(casts, "ultimateCasts"),
        };
    }

    private static int ReadIntProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var valueEl)
            && valueEl.TryGetInt32(out var parsed)
            ? parsed
            : 0;
    }

    private static Dictionary<string, string> BuildTeamByPuuid(JsonElement root)
    {
        var teamByPuuid = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!root.TryGetProperty("players", out var players)
            || players.ValueKind != JsonValueKind.Array)
        {
            return teamByPuuid;
        }

        foreach (var player in players.EnumerateArray())
        {
            var puuid = player.TryGetProperty("puuid", out var puuidEl)
                ? puuidEl.GetString()
                : null;
            var teamId = player.TryGetProperty("teamId", out var teamEl)
                ? teamEl.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(puuid) && !string.IsNullOrWhiteSpace(teamId))
            {
                teamByPuuid[puuid] = teamId;
            }
        }

        return teamByPuuid;
    }
}
