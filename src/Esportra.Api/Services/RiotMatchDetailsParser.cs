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
        object? MatchInfo,
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
            playerList.Add(ValorantMatchStatsHelper.BuildPlayerPayload(
                p,
                roundAggregates,
                team1Puuids,
                team2Puuids));
        }

        return new ParsedRiotMatch(
            playerList,
            new { roundsWon = blueRounds, won = blueWon },
            new { roundsWon = redRounds, won = redWon },
            gameLengthMillis,
            gameStartMillis,
            ValorantMatchStatsHelper.BuildMatchInfoPayload(root),
            ValorantMatchStatsHelper.BuildDerivedMatchDetails(root));
    }
}
