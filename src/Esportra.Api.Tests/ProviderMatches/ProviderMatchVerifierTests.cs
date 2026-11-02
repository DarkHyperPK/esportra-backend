using Esportra.Api.Services.ProviderMatches;
using FluentAssertions;
using Xunit;
using static Esportra.Api.Tests.ProviderMatches.ValorantMatchFixture;

namespace Esportra.Api.Tests.ProviderMatches;

public sealed class ProviderMatchVerifierTests
{
    private static NormalizedValorantMatch Match(
        string map = "/Game/Maps/Ascent/Ascent", string flow = "CustomGame", bool completed = true,
        IEnumerable<int>? observers = null) =>
        ValorantMatchNormalizer.Normalize(Build(mapId: map, provisioningFlowId: flow, isCompleted: completed, observers: observers))!;

    /// <summary>Team 1 = p1..p5, team 2 = p6..p10, all linked.</summary>
    private static List<RosterMember> FullRoster() =>
        Enumerable.Range(1, 10).Select(i => new RosterMember(Guid.NewGuid(), i <= 5 ? 1 : 2, Puuid(i))).ToList();

    private static VerificationInput Input(NormalizedValorantMatch m, List<RosterMember> roster, int? s1 = 13, int? s2 = 7, string? map = "Ascent") =>
        new(m, s1, s2, map, roster);

    [Fact]
    public void All_Players_Linked_And_Report_Agrees_Is_Verified()
    {
        var r = ProviderMatchVerifier.Verify(Input(Match(), FullRoster()));
        r.Status.Should().Be("verified");
        (r.Team1Side, r.Team2Side).Should().Be(("Blue", "Red"));
        r.Players.Should().OnlyContain(p => p.Slot != null);
    }

    [Fact]
    public void Teams_May_Be_On_Either_Riot_Side()
    {
        var roster = Enumerable.Range(1, 10).Select(i => new RosterMember(Guid.NewGuid(), i <= 5 ? 2 : 1, Puuid(i))).ToList();
        var r = ProviderMatchVerifier.Verify(Input(Match(), roster, s1: 7, s2: 13));
        r.Status.Should().Be("verified");
        (r.Team1Side, r.Team2Side).Should().Be(("Red", "Blue"));
    }

    [Fact]
    public void Unlinked_Players_Make_It_Partially_Verified()
    {
        var roster = FullRoster().Select(m => m.Puuid is "p5" or "p10" ? m with { Puuid = null } : m).ToList();
        var r = ProviderMatchVerifier.Verify(Input(Match(), roster));
        r.Status.Should().Be("partially_verified");
        r.Reasons.Should().Contain("unmatched_players:2");
        r.Players.Count(p => p.Slot is null).Should().Be(2);
    }

    [Fact]
    public void One_Team_Linked_Is_Enough_To_Find_Both_Sides()
    {
        var roster = FullRoster().Where(m => m.Slot == 1).ToList();
        var r = ProviderMatchVerifier.Verify(Input(Match(), roster));
        r.Status.Should().Be("partially_verified");
        (r.Team1Side, r.Team2Side).Should().Be(("Blue", "Red"));
    }

    [Fact]
    public void No_Roster_Account_In_The_Match_Is_Unverified_Not_Disputed()
    {
        var r = ProviderMatchVerifier.Verify(Input(Match(), [new RosterMember(Guid.NewGuid(), 1, "someone-else")]));
        r.Status.Should().Be("unverified");
        r.Reasons.Should().Contain("no_roster_account_in_match");
    }

    [Fact]
    public void Wrong_Score_Is_Disputed()
    {
        var r = ProviderMatchVerifier.Verify(Input(Match(), FullRoster(), s1: 13, s2: 11));
        r.Status.Should().Be("disputed");
        r.Reasons.Should().Contain("team2_score_mismatch");
    }

    [Fact]
    public void Swapped_Score_Is_Disputed()
    {
        ProviderMatchVerifier.Verify(Input(Match(), FullRoster(), s1: 7, s2: 13)).Status.Should().Be("disputed");
    }

    [Fact]
    public void Wrong_Map_Is_Disputed_But_Free_Text_Map_Is_Not_Compared()
    {
        ProviderMatchVerifier.Verify(Input(Match(), FullRoster(), map: "Bind")).Reasons.Should().Contain("map_mismatch");
        ProviderMatchVerifier.Verify(Input(Match(), FullRoster(), map: "scrim map 1")).Status.Should().Be("verified");
        ProviderMatchVerifier.Verify(Input(Match(map: "/Game/Maps/Triad/Triad"), FullRoster(), map: "Haven")).Status.Should().Be("verified");
    }

    [Fact]
    public void Not_A_Custom_Game_Is_Disputed() =>
        ProviderMatchVerifier.Verify(Input(Match(flow: "Matchmaking"), FullRoster())).Reasons.Should().Contain("not_custom_game");

    [Fact]
    public void A_Teammate_On_The_Other_Side_Splits_The_Team_And_Is_Disputed()
    {
        var roster = FullRoster().Select(m => m.Puuid == "p6" ? m with { Slot = 1 } : m).ToList();
        var r = ProviderMatchVerifier.Verify(Input(Match(), roster));
        r.Status.Should().Be("disputed");
        r.Reasons.Should().Contain("team1_split_across_sides");
    }

    [Fact]
    public void Both_Teams_On_The_Same_Side_Is_Disputed()
    {
        // Team 1 is p1-p3 and team 2 is p4: all Blue, so both teams map to the same Riot side.
        var sameSide = Enumerable.Range(1, 3).Select(i => new RosterMember(Guid.NewGuid(), 1, Puuid(i)))
            .Concat(new[] { new RosterMember(Guid.NewGuid(), 2, "p4") }).ToList();
        ProviderMatchVerifier.Verify(Input(Match(), sameSide)).Reasons.Should().Contain("teams_on_same_side");
    }

    [Fact]
    public void Unfinished_Match_Stays_Pending() =>
        ProviderMatchVerifier.Verify(Input(Match(completed: false), FullRoster())).Status.Should().Be("pending");

    [Fact]
    public void Observers_Are_Ignored()
    {
        var m = Match(observers: [10]);
        var roster = FullRoster().Where(r => r.Puuid != "p10").ToList();
        ProviderMatchVerifier.Verify(Input(m, roster)).Status.Should().Be("verified");
    }

    [Fact]
    public void Same_Input_Gives_Same_Result()
    {
        var input = Input(Match(), FullRoster());
        var a = ProviderMatchVerifier.Verify(input);
        var b = ProviderMatchVerifier.Verify(input);
        (a.Status, a.Team1Side, a.Reasons.Count).Should().Be((b.Status, b.Team1Side, b.Reasons.Count));
    }
}
