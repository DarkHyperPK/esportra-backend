using System.Text.Json;
using Esportra.Api.Services;

namespace Esportra.Api.Tests.ScoreboardOcr;

/// <summary>Riot VAL-MATCH-V1 match records for tests, built through the real parser.</summary>
internal static class RiotMatchSamples
{
    internal sealed record Player(string Puuid, string Name, string Tag, string Side, int Kills = 10, int Deaths = 10, int Assists = 5);

    public static RiotMatch Match(string matchId, string map, int blueRounds, int redRounds, DateTimeOffset startedAt, params Player[] players)
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            matchInfo = new
            {
                matchId,
                mapId = $"/Game/Maps/{map}/{map}",
                queueId = "custom",
                gameStartMillis = startedAt.ToUnixTimeMilliseconds(),
                gameLengthMillis = 2_400_000,
            },
            teams = new object[] { new { teamId = "Blue", roundsWon = blueRounds, won = blueRounds > redRounds }, new { teamId = "Red", roundsWon = redRounds, won = redRounds > blueRounds } },
            players = players.Select(p => new
            {
                puuid = p.Puuid,
                gameName = p.Name,
                tagLine = p.Tag,
                teamId = p.Side,
                characterId = "add6443a-41bd-e414-f6ad-e58d267f4e95",
                stats = new { kills = p.Kills, deaths = p.Deaths, assists = p.Assists, score = 4200, roundsPlayed = blueRounds + redRounds },
            }),
        });
        return RiotMatchLookup.ParseMatch(json);
    }
}
