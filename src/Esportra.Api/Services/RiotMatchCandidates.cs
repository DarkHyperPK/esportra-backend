using System.Text.Json;

namespace Esportra.Api.Services;

/// <summary>
/// A Riot custom game as a result candidate: the shape "Auto-fetch" lists and submits (scores from the
/// reporter's side, Riot's players with full stats, round and economy timelines). Shared by the match
/// history scan and the screenshot upload, so a game found either way is reported the same way.
/// </summary>
public static class RiotMatchCandidates
{
    /// <summary>The candidate as seen by <paramref name="scannerPuuid"/>; null when that player isn't in the game.</summary>
    public static object? Build(
        JsonElement root, string riotMatchId, string scannerPuuid,
        HashSet<string> team1Puuids, HashSet<string> team2Puuids, bool isExpectedMap = false)
    {
        var info = root.GetProperty("matchInfo");
        var riotMapId = info.GetProperty("mapId").GetString() ?? "";
        var mapDisplayName = ValorantMapIds.Resolve(riotMapId);
        var queueId = info.GetProperty("queueId").GetString() ?? "";
        var gameLengthMillis = info.GetProperty("gameLengthMillis").GetInt64();
        var gameStartMillis = info.GetProperty("gameStartMillis").GetInt64();

        // Parse teams
        int blueRounds = 0, redRounds = 0;
        bool blueWon = false, redWon = false;
        foreach (var team in root.GetProperty("teams").EnumerateArray())
        {
            var tid = team.GetProperty("teamId").GetString();
            var won = team.GetProperty("won").GetBoolean();
            var rw = team.GetProperty("roundsWon").GetInt32();
            if (tid == "Blue") { blueRounds = rw; blueWon = won; }
            else if (tid == "Red") { redRounds = rw; redWon = won; }
        }

        // Parse players, find scanner
        string? scannerSide = null, scannerAgent = null;
        int kills = 0, deaths = 0, assists = 0;
        var playerList = new List<object>();
        var roundAggregates = ValorantMatchStatsHelper.BuildRoundAggregates(root);

        foreach (var p in root.GetProperty("players").EnumerateArray())
        {
            var pPuuid = p.GetProperty("puuid").GetString() ?? "";
            playerList.Add(ValorantMatchStatsHelper.BuildPlayerPayload(
                p,
                roundAggregates,
                team1Puuids,
                team2Puuids));

            if (pPuuid == scannerPuuid)
            {
                scannerSide = p.GetProperty("teamId").GetString() ?? "";
                scannerAgent = p.GetProperty("characterId").GetString() ?? "";
                var stats = p.GetProperty("stats");
                kills = stats.GetProperty("kills").GetInt32();
                deaths = stats.GetProperty("deaths").GetInt32();
                assists = stats.GetProperty("assists").GetInt32();
            }
        }

        if (scannerSide is null) return null; // the reporter isn't in this match

        var isBlue = scannerSide == "Blue";
        var myRounds = isBlue ? blueRounds : redRounds;
        var enemyRounds = isBlue ? redRounds : blueRounds;
        var didWin = isBlue ? blueWon : redWon;

        var derivedDetails = ValorantMatchStatsHelper.BuildDerivedMatchDetails(root);
        var serializedDerived = ValorantMatchStatsHelper.SerializeDerivedDetails(derivedDetails);

        return new
        {
            id = riotMatchId,
            map = mapDisplayName,
            mapId = riotMapId,
            queueId,
            startTime = gameStartMillis,
            gameLengthMillis,
            myTeamScore = myRounds,
            enemyTeamScore = enemyRounds,
            reporterSide = scannerSide,
            score = $"{myRounds}-{enemyRounds}",
            result = didWin ? "Victory" : "Defeat",
            kda = $"{kills}/{deaths}/{assists}",
            agent = scannerAgent,
            isExpectedMap,
            blueTeam = new { roundsWon = blueRounds, won = blueWon },
            redTeam = new { roundsWon = redRounds, won = redWon },
            players = playerList,
            matchInfo = ValorantMatchStatsHelper.BuildMatchInfoPayload(root),
            roundTimeline = serializedDerived.RoundTimeline,
            economyTimeline = serializedDerived.EconomyTimeline,
            weaponSummaries = serializedDerived.WeaponSummaries,
        };
    }
}
