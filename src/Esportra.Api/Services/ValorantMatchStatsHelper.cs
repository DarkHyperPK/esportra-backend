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

    internal sealed record RoundTimelineEntry(int Round, string WinningTeam, string? ResultCode);

    internal sealed record EconomyTimelineEntry(int Round, int BlueSpent, int RedSpent);

    internal sealed record DerivedMatchDetails(
        IReadOnlyList<RoundTimelineEntry> RoundTimeline,
        IReadOnlyList<EconomyTimelineEntry> EconomyTimeline);

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

            string? firstBloodPuuid = null;
            var earliestKillMs = long.MaxValue;

            foreach (var roundPlayer in playerStats.EnumerateArray())
            {
                var puuid = roundPlayer.TryGetProperty("puuid", out var puuidEl)
                    ? puuidEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(puuid)) continue;

                if (!aggregates.TryGetValue(puuid, out var current))
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

                        if (damageEntry.TryGetProperty("headshots", out var headshots)
                            && headshots.TryGetInt32(out var headshotCount))
                        {
                            roundHeadshots += headshotCount;
                        }

                        if (damageEntry.TryGetProperty("bodyshots", out var bodyshots)
                            && bodyshots.TryGetInt32(out var bodyshotCount))
                        {
                            roundBodyshots += bodyshotCount;
                        }

                        if (damageEntry.TryGetProperty("legshots", out var legshots)
                            && legshots.TryGetInt32(out var legshotCount))
                        {
                            roundLegshots += legshotCount;
                        }
                    }
                }

                aggregates[puuid] = current with
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

                        if (killTime >= earliestKillMs) continue;

                        earliestKillMs = killTime;
                        firstBloodPuuid = puuid;
                    }
                }
            }

            if (firstBloodPuuid is null || !aggregates.TryGetValue(firstBloodPuuid, out var fbCurrent))
            {
                continue;
            }

            aggregates[firstBloodPuuid] = fbCurrent with
            {
                FirstBloods = fbCurrent.FirstBloods + 1,
            };
        }

        return aggregates;
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
            BuildEconomyTimeline(root));
    }

    internal static List<RoundTimelineEntry> BuildRoundTimeline(JsonElement root)
    {
        var timeline = new List<RoundTimelineEntry>();

        if (!root.TryGetProperty("roundResults", out var roundResults)
            || roundResults.ValueKind != JsonValueKind.Array)
        {
            return timeline;
        }

        var roundNumber = 0;
        foreach (var round in roundResults.EnumerateArray())
        {
            roundNumber++;
            var winningTeam = round.TryGetProperty("winningTeam", out var winningTeamEl)
                ? winningTeamEl.GetString() ?? string.Empty
                : string.Empty;
            var resultCode = round.TryGetProperty("roundResultCode", out var resultCodeEl)
                ? resultCodeEl.GetString()
                : null;

            timeline.Add(new RoundTimelineEntry(roundNumber, winningTeam, resultCode));
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

        var roundNumber = 0;
        foreach (var round in roundResults.EnumerateArray())
        {
            roundNumber++;
            var blueSpent = 0;
            var redSpent = 0;

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

                    var spent = 0;
                    if (roundPlayer.TryGetProperty("economy", out var economyEl)
                        && economyEl.TryGetProperty("spent", out var spentEl)
                        && spentEl.TryGetInt32(out var parsedSpent))
                    {
                        spent = parsedSpent;
                    }

                    if (string.Equals(teamId, "Blue", StringComparison.OrdinalIgnoreCase))
                    {
                        blueSpent += spent;
                    }
                    else if (string.Equals(teamId, "Red", StringComparison.OrdinalIgnoreCase))
                    {
                        redSpent += spent;
                    }
                }
            }

            timeline.Add(new EconomyTimelineEntry(roundNumber, blueSpent, redSpent));
        }

        return timeline;
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
