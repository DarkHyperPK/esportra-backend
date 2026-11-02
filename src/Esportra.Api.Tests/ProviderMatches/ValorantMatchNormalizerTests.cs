using Esportra.Api.Services.ProviderMatches;
using FluentAssertions;
using Xunit;
using static Esportra.Api.Tests.ProviderMatches.ValorantMatchFixture;

namespace Esportra.Api.Tests.ProviderMatches;

public sealed class ValorantMatchNormalizerTests
{
    [Fact]
    public void Match_Info_Custom_Game_And_Teams_Are_Read()
    {
        var m = ValorantMatchNormalizer.Normalize(Build())!;
        m.Info.ProviderMatchId.Should().Be("11111111-2222-3333-4444-555555555555");
        m.Info.IsCustom.Should().BeTrue();
        m.Info.IsCompleted.Should().BeTrue();
        m.Info.StartedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_761_000_000_000L));
        m.Teams.Select(t => (t.TeamId, t.RoundsWon)).Should().Equal(("Blue", 13), ("Red", 7));
        m.Players.Should().HaveCount(10);
    }

    [Theory]
    [InlineData("CustomGame", "", true)]
    [InlineData("Matchmaking", "competitive", false)]
    [InlineData("Matchmaking", "custom", true)]
    public void Custom_Game_Detection(string flow, string queue, bool expected) =>
        ValorantMatchNormalizer.Normalize(Build(provisioningFlowId: flow, queueId: queue))!.Info.IsCustom.Should().Be(expected);

    [Fact]
    public void Missing_Match_Id_Returns_Null() =>
        ValorantMatchNormalizer.Normalize(System.Text.Json.JsonDocument.Parse("{\"matchInfo\":{}}").RootElement).Should().BeNull();

    [Fact]
    public void Opening_Kill_Is_Earliest_In_Round_Regardless_Of_Source_Order()
    {
        // p2's kill is nested before p1's in the JSON but happens later in the round.
        var round = new Round(0, "Blue", "Attacker", [
            new Kill("p1", "p6", RoundMs: 20_000, GameMs: 60_000),
            new Kill("p2", "p7", RoundMs: 25_000, GameMs: 65_000),
        ]);
        var m = ValorantMatchNormalizer.Normalize(Build(rounds: [round]))!;
        m.Kills.Select(k => (k.Killer, k.Ordinal)).Should().Equal(("p1", 1), ("p2", 2));
        m.Players.Single(p => p.Puuid == "p1").FirstKills.Should().Be(1);
        m.Players.Single(p => p.Puuid == "p6").FirstDeaths.Should().Be(1);
        m.Players.Single(p => p.Puuid == "p2").FirstKills.Should().Be(0);
    }

    [Fact]
    public void Simultaneous_Kills_Break_Ties_Deterministically_By_Killer_Then_Victim()
    {
        var round = new Round(0, "Blue", "Attacker", [
            new Kill("p3", "p6", 10_000, 50_000),
            new Kill("p1", "p8", 10_000, 50_000),
            new Kill("p1", "p7", 10_000, 50_000),
        ]);
        var m = ValorantMatchNormalizer.Normalize(Build(rounds: [round]))!;
        m.Kills.Select(k => (k.Killer, k.Victim)).Should().Equal(("p1", "p7"), ("p1", "p8"), ("p3", "p6"));
    }

    [Fact]
    public void Multi_Kills_Count_Within_A_Round_Only()
    {
        var r0 = new Round(0, "Blue", "Attacker", [
            new Kill("p1", "p6", 1_000, 1_000), new Kill("p1", "p7", 2_000, 2_000), new Kill("p1", "p8", 3_000, 3_000),
        ]);
        var r1 = new Round(1, "Blue", "Attacker", [new Kill("p1", "p6", 1_000, 100_000), new Kill("p1", "p7", 2_000, 101_000)]);
        var r2 = new Round(2, "Blue", "Attacker", [new Kill("p1", "p6", 1_000, 200_000)]);
        var p1 = ValorantMatchNormalizer.Normalize(Build(rounds: [r0, r1, r2]))!.Players.Single(p => p.Puuid == "p1");
        (p1.Kills2K, p1.Kills3K, p1.Kills4K, p1.Kills5K).Should().Be((1, 1, 0, 0));
    }

    [Fact]
    public void Five_Kills_In_A_Round_Is_An_Ace()
    {
        var kills = Enumerable.Range(6, 5).Select((v, i) => new Kill("p1", Puuid(v), 1_000 * (i + 1), 1_000 * (i + 1))).ToArray();
        var p1 = ValorantMatchNormalizer.Normalize(Build(rounds: [new Round(0, "Blue", "Attacker", kills)]))!.Players.Single(p => p.Puuid == "p1");
        p1.Kills5K.Should().Be(1);
        p1.Kills2K.Should().Be(0);
    }

    [Fact]
    public void Damage_And_Shot_Locations_Are_Summed_Per_Player()
    {
        var round = new Round(0, "Blue", "Attacker", [], Damage: [
            new Dmg("p1", "p6", 150, 1, 1, 0), new Dmg("p1", "p7", 60, 0, 2, 1), new Dmg("p2", "p6", 40, 0, 1, 0),
        ]);
        var m = ValorantMatchNormalizer.Normalize(Build(rounds: [round]))!;
        var p1 = m.Players.Single(p => p.Puuid == "p1");
        (p1.DamageDealt, p1.Headshots, p1.Bodyshots, p1.Legshots).Should().Be((210, 1, 3, 1));
        m.Damage.Should().HaveCount(3);
    }

    [Fact]
    public void Plants_And_Defuses_Keep_Original_Round_Data()
    {
        var planted = new Round(0, "Red", "Defender", [], Planter: "p6", Defuser: "p1");
        var m = ValorantMatchNormalizer.Normalize(Build(rounds: [planted, new Round(1, "Blue", "Attacker", [])]))!;
        m.Players.Single(p => p.Puuid == "p6").Plants.Should().Be(1);
        m.Players.Single(p => p.Puuid == "p1").Defuses.Should().Be(1);
        var r0 = m.Rounds.Single(r => r.RoundNum == 0);
        (r0.PlantSite, r0.PlantX, r0.PlantY, r0.DefuseX, r0.WinningTeamRole).Should().Be(("A", 10, 20, 11, "Defender"));
        m.Rounds.Single(r => r.RoundNum == 1).BombPlanter.Should().BeNull();
    }

    [Fact]
    public void Normalizing_Twice_Gives_Identical_Output()
    {
        var round = new Round(0, "Blue", "Attacker", [new Kill("p1", "p6", 5_000, 5_000), new Kill("p2", "p7", 6_000, 6_000)],
            Damage: [new Dmg("p1", "p6", 150, 1, 0, 0)], Planter: "p1");
        var json = Build(rounds: [round]);
        var a = ValorantMatchNormalizer.Normalize(json)!;
        var b = ValorantMatchNormalizer.Normalize(json)!;
        System.Text.Json.JsonSerializer.Serialize(a).Should().Be(System.Text.Json.JsonSerializer.Serialize(b));
    }

    [Fact]
    public void Economy_And_Ability_Are_Kept_Per_Round_Per_Player()
    {
        var m = ValorantMatchNormalizer.Normalize(Build(rounds: [new Round(0, "Blue", "Attacker", [])]))!;
        m.RoundPlayerStats.Should().HaveCount(10);
        var s = m.RoundPlayerStats.First();
        (s.LoadoutValue, s.Weapon, s.Armor, s.Spent, s.Remaining).Should().Be((4000, "Vandal", "Heavy", 3900, 1000));
    }
}
