using System.Text.Json;
using System.Text.Json.Nodes;

namespace Esportra.Api.Tests.ProviderMatches;

/// <summary>Builds VAL-MATCH-V1 shaped JSON. Players are "p1".."p10"; p1-p5 are Blue, p6-p10 Red.</summary>
internal static class ValorantMatchFixture
{
    internal sealed record Kill(string Killer, string Victim, int RoundMs, int GameMs, string[]? Assists = null);
    internal sealed record Dmg(string From, string To, int Damage, int Head, int Body, int Leg);
    internal sealed record Round(
        int Num, string Winner, string Role, Kill[] Kills, Dmg[]? Damage = null, string? Planter = null, string? Defuser = null);

    public static string Puuid(int n) => $"p{n}";
    public static string Side(int n) => n <= 5 ? "Blue" : "Red";

    public static JsonElement Build(
        string matchId = "11111111-2222-3333-4444-555555555555",
        string mapId = "/Game/Maps/Ascent/Ascent",
        string provisioningFlowId = "CustomGame",
        string queueId = "",
        bool isCompleted = true,
        int blueRounds = 13,
        int redRounds = 7,
        IEnumerable<Round>? rounds = null,
        IEnumerable<int>? observers = null)
    {
        var roundList = (rounds ?? []).ToList();
        var obs = observers?.ToHashSet() ?? [];
        var players = new JsonArray();
        for (var i = 1; i <= 10; i++)
        {
            players.Add(new JsonObject
            {
                ["puuid"] = Puuid(i),
                ["gameName"] = $"Player{i}",
                ["tagLine"] = "EUW",
                ["teamId"] = Side(i),
                ["partyId"] = "party",
                ["characterId"] = "add6443a-41bd-e414-f6ad-e58d267f4e95",
                ["competitiveTier"] = 0,
                ["isObserver"] = obs.Contains(i),
                ["stats"] = new JsonObject
                {
                    ["score"] = 4000,
                    ["roundsPlayed"] = blueRounds + redRounds,
                    ["kills"] = 10,
                    ["deaths"] = 8,
                    ["assists"] = 3,
                    ["playtimeMillis"] = 1_800_000,
                    ["abilityCasts"] = new JsonObject { ["grenadeCasts"] = 5, ["ability1Casts"] = 6, ["ability2Casts"] = 7, ["ultimateCasts"] = 1 },
                },
            });
        }

        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["matchInfo"] = new JsonObject
            {
                ["matchId"] = matchId,
                ["mapId"] = mapId,
                ["gameVersion"] = "release-10.00",
                ["gameLengthMillis"] = 2_000_000,
                ["region"] = "eu",
                ["gameStartMillis"] = 1_761_000_000_000L,
                ["provisioningFlowId"] = provisioningFlowId,
                ["isCompleted"] = isCompleted,
                ["customGameName"] = "",
                ["queueId"] = queueId,
                ["gameMode"] = "/Game/GameModes/Bomb/BombGameMode.BombGameMode_C",
                ["isRanked"] = false,
                ["seasonId"] = "season",
            },
            ["players"] = players,
            ["teams"] = new JsonArray(
                new JsonObject { ["teamId"] = "Blue", ["won"] = blueRounds > redRounds, ["roundsPlayed"] = blueRounds + redRounds, ["roundsWon"] = blueRounds, ["numPoints"] = blueRounds },
                new JsonObject { ["teamId"] = "Red", ["won"] = redRounds > blueRounds, ["roundsPlayed"] = blueRounds + redRounds, ["roundsWon"] = redRounds, ["numPoints"] = redRounds }),
            ["roundResults"] = new JsonArray(roundList.Select(RoundJson).ToArray()),
        });
    }

    private static JsonNode RoundJson(Round r)
    {
        var playerStats = new JsonArray();
        for (var i = 1; i <= 10; i++)
        {
            var puuid = Puuid(i);
            playerStats.Add(new JsonObject
            {
                ["puuid"] = puuid,
                ["kills"] = new JsonArray(r.Kills.Where(k => k.Killer == puuid).Select(k => (JsonNode)new JsonObject
                {
                    ["timeSinceGameStartMillis"] = k.GameMs,
                    ["timeSinceRoundStartMillis"] = k.RoundMs,
                    ["killer"] = k.Killer,
                    ["victim"] = k.Victim,
                    ["victimLocation"] = new JsonObject { ["x"] = 100, ["y"] = 200 },
                    ["assistants"] = new JsonArray((k.Assists ?? []).Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                    ["playerLocations"] = new JsonArray(),
                    ["finishingDamage"] = new JsonObject { ["damageType"] = "Weapon", ["damageItem"] = "Vandal", ["isSecondaryFireMode"] = false },
                }).ToArray()),
                ["damage"] = new JsonArray((r.Damage ?? []).Where(d => d.From == puuid).Select(d => (JsonNode)new JsonObject
                {
                    ["receiver"] = d.To,
                    ["damage"] = d.Damage,
                    ["headshots"] = d.Head,
                    ["bodyshots"] = d.Body,
                    ["legshots"] = d.Leg,
                }).ToArray()),
                ["score"] = 200,
                ["economy"] = new JsonObject { ["loadoutValue"] = 4000, ["weapon"] = "Vandal", ["armor"] = "Heavy", ["remaining"] = 1000, ["spent"] = 3900 },
                ["ability"] = new JsonObject { ["grenadeEffects"] = null, ["ability1Effects"] = null },
            });
        }

        var node = new JsonObject
        {
            ["roundNum"] = r.Num,
            ["roundResult"] = "Elimination",
            ["roundCeremony"] = "CeremonyDefault",
            ["winningTeam"] = r.Winner,
            ["winningTeamRole"] = r.Role,
            ["roundResultCode"] = "Elimination",
            ["playerStats"] = playerStats,
        };
        if (r.Planter is not null)
        {
            node["bombPlanter"] = r.Planter;
            node["plantRoundTime"] = 30_000;
            node["plantSite"] = "A";
            node["plantLocation"] = new JsonObject { ["x"] = 10, ["y"] = 20 };
        }
        if (r.Defuser is not null)
        {
            node["bombDefuser"] = r.Defuser;
            node["defuseRoundTime"] = 80_000;
            node["defuseLocation"] = new JsonObject { ["x"] = 11, ["y"] = 21 };
        }
        return node;
    }
}
