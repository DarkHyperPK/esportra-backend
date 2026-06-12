using System.Text.Json;

namespace Esportra.Api.Services;

internal static class RiotMatchDetailsParser
{
    internal sealed record ParsedRiotMatch(
        List<object> Players,
        object BlueTeam,
        object RedTeam,
        long GameLengthMillis,
        long StartTime,
        ValorantMatchStatsHelper.DerivedMatchDetails Derived);

    internal static ParsedRiotMatch? Parse(
        JsonElement root,
        HashSet<string> team1Puuids,
        HashSet<string> team2Puuids)
    {
        if (!root.TryGetProperty("matchInfo", out var info))
        {
            return null;
        }

        var gameLengthMillis = info.TryGetProperty("gameLengthMillis", out var lengthEl)
            && lengthEl.TryGetInt64(out var parsedLength)
            ? parsedLength
            : 0L;
        var gameStartMillis = info.TryGetProperty("gameStartMillis", out var startEl)
            && startEl.TryGetInt64(out var parsedStart)
            ? parsedStart
            : 0L;

        int blueRounds = 0, redRounds = 0;
        bool blueWon = false, redWon = false;

        if (root.TryGetProperty("teams", out var teams) && teams.ValueKind == JsonValueKind.Array)
        {
            foreach (var team in teams.EnumerateArray())
            {
                var tid = team.GetProperty("teamId").GetString();
                var won = team.GetProperty("won").GetBoolean();
                var rw = team.GetProperty("roundsWon").GetInt32();
                if (tid == "Blue") { blueRounds = rw; blueWon = won; }
                else if (tid == "Red") { redRounds = rw; redWon = won; }
            }
        }

        var playerList = new List<object>();
        var roundAggregates = ValorantMatchStatsHelper.BuildRoundAggregates(root);

        if (!root.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var p in players.EnumerateArray())
        {
            var pPuuid = p.GetProperty("puuid").GetString() ?? "";
            var pTeam = p.GetProperty("teamId").GetString() ?? "";
            var pName = p.TryGetProperty("gameName", out var gn) ? gn.GetString() ?? "" : "";
            var pTag = p.TryGetProperty("tagLine", out var tl) ? tl.GetString() ?? "" : "";
            var charId = p.GetProperty("characterId").GetString() ?? "";
            var stats = p.GetProperty("stats");
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
                adr = ValorantMatchStatsHelper.ComputeAdr(roundStats.TotalDamage, roundsPlayed);
                hsPct = ValorantMatchStatsHelper.ComputeHeadshotPercent(
                    roundStats.Headshots,
                    roundStats.Bodyshots,
                    roundStats.Legshots);
                firstBloods = roundStats.FirstBloods;
            }

            playerList.Add(new
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
                acs = ValorantMatchStatsHelper.ComputeAcs(pScore, roundsPlayed),
                adr,
                hsPct,
                kdRatio = ValorantMatchStatsHelper.ComputeKdRatio(pK, pD),
                firstBloods,
                isTeam1 = team1Puuids.Contains(pPuuid),
                isTeam2 = team2Puuids.Contains(pPuuid),
            });
        }

        return new ParsedRiotMatch(
            playerList,
            new { roundsWon = blueRounds, won = blueWon },
            new { roundsWon = redRounds, won = redWon },
            gameLengthMillis,
            gameStartMillis,
            ValorantMatchStatsHelper.BuildDerivedMatchDetails(root));
    }
}
