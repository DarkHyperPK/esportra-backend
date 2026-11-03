using System.Text.Json;
using Esportra.Api.Services.ScrimData;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.ScrimData;

public sealed class ScrimDataBuilderTests
{
    private static readonly Guid User1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid User2 = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static ScrimMapRow Row(
        string source = "manual", string? map = "Ascent", int s1 = 13, int s2 = 7, int slot = 1, int? winner = null, Guid? id = null,
        string? association = null, string? matchStatus = null, string? ocr = null) =>
        new(id ?? Guid.NewGuid(), Guid.NewGuid(), 1, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), source, map, s1, s2, winner ?? (s1 > s2 ? 1 : 2), slot,
            "Omega", association, matchStatus, ocr);

    private static string Ocr(params object[] players) => JsonSerializer.Serialize(new { source = "ocr", players });

    private static object Player(bool team1, string name, string? agent, Guid? user = null, int kills = 20, int deaths = 10, int assists = 5, double acs = 250) =>
        new { gameName = name, characterId = agent, isTeam1 = team1, isTeam2 = !team1, userId = user?.ToString(), kills, deaths, assists, acs, adr = 150.0, hsPct = 25.0, firstBloods = 3, plants = 1, defuses = 0 };

    private static ScrimDataSet Build(IReadOnlyList<ScrimMapRow> rows, Dictionary<Guid, RiotGameRow>? games = null, List<RiotPlayerRow>? players = null, int awaiting = 0, int disputed = 0) =>
        ScrimDataBuilder.Build(rows, games ?? [], players ?? [], awaiting, disputed);

    [Fact]
    public void A_manual_map_counts_for_the_result_and_the_map_only()
    {
        var game = Build([Row(map: "ascent")]).Games.Single();
        (game.Tier, game.Result, game.Map).Should().Be((DataTier.ScoreOnly, "win", "Ascent"));
        game.Lines.Should().BeEmpty();
        game.Detail.Agents.Should().BeEmpty();
        (game.Detail.AttackPlayed, game.Detail.DefensePlayed).Should().Be((0, 0));
    }

    [Fact]
    public void A_screenshot_map_carries_only_the_scoreboard_of_the_teams_own_side()
    {
        var ocr = Ocr(Player(true, "Mine", "agent-1", User1), Player(true, "Mate", "agent-2"), Player(false, "Theirs", "agent-9"));
        var game = Build([Row("ocr", ocr: ocr)]).Games.Single();

        game.Tier.Should().Be(DataTier.Screenshot);
        game.Lines.Select(l => l.Name).Should().Equal("Mine", "Mate");
        game.Lines[0].Should().Match<ScrimPlayerLine>(l => l.UserId == User1 && l.Kills == 20 && l.Acs == 250 && l.FirstKills == 3 && l.FirstDeaths == null && l.RoundsPlayed == 20);
        game.Detail.Agents.Should().Equal("agent-1", "agent-2");
    }

    [Fact]
    public void The_second_team_reads_its_own_players()
    {
        var ocr = Ocr(Player(true, "One", "agent-1"), Player(false, "Two", "agent-2", User2));
        var game = Build([Row("ocr", slot: 2, ocr: ocr)]).Games.Single();
        game.Lines.Should().ContainSingle().Which.UserId.Should().Be(User2);
    }

    [Fact]
    public void A_screenshot_with_no_usable_scoreboard_falls_back_to_score_only_instead_of_failing()
    {
        Build([Row("ocr", ocr: "not json")]).Games.Single().Tier.Should().Be(DataTier.ScoreOnly);
        Build([Row("ocr", ocr: null)]).Games.Single().Tier.Should().Be(DataTier.ScoreOnly);
        Build([Row("ocr", ocr: Ocr(Player(false, "OnlyThem", "a")))]).Games.Single().Tier.Should().Be(DataTier.ScoreOnly);
    }

    private static (ScrimMapRow Row, Dictionary<Guid, RiotGameRow> Games, List<RiotPlayerRow> Players) RiotMap(string association = "verified")
    {
        var row = Row("riot", map: "typed name", association: association, matchStatus: "retrieved");
        var games = new Dictionary<Guid, RiotGameRow> { [row.GameId] = new(row.GameId, "/Game/Maps/Duality/Duality", 20, ["a", "b", "c", "d", "e"], 12, 7, 8, 6) };
        var players = new List<RiotPlayerRow>
        {
            new(row.GameId, User1, "One", "AGENT-A", "member", 18, 9, 4, 5000, 20, 3000, 10, 30, 10, 3, 1, 1, 0),
            new(row.GameId, null, "Unlinked", "agent-b", null, 5, 12, 1, 2000, 20, 1200, 0, 0, 0, 0, 2, 0, 0),
        };
        return (row, games, players);
    }

    [Fact]
    public void A_verified_riot_map_has_full_detail_and_uses_riots_map()
    {
        var (row, games, players) = RiotMap();
        var game = Build([row], games, players).Games.Single();

        game.Tier.Should().Be(DataTier.Full);
        game.Map.Should().Be("Bind");   // Riot's name wins over what was typed
        game.Lines[0].Should().Match<ScrimPlayerLine>(l => l.Acs == 250.0 && l.Adr == 150.0 && l.HsPct == 20.0 && l.FirstDeaths == 1 && l.CharacterId == "agent-a");
        (game.Detail.AttackPlayed, game.Detail.AttackWon, game.Detail.DefensePlayed, game.Detail.DefenseWon).Should().Be((12, 7, 8, 6));
        game.Lines[1].HsPct.Should().BeNull("no located hits means no share, not 0");
    }

    [Theory]
    [InlineData("disputed", "retrieved", 0, 1)]
    [InlineData("duplicate", "retrieved", 0, 1)]
    [InlineData("unverified", "unavailable", 0, 1)]
    [InlineData(null, null, 1, 0)]
    [InlineData("pending", "pending", 1, 0)]
    public void A_riot_map_that_cannot_be_trusted_still_counts_for_the_result_and_says_why(string? association, string? matchStatus, int pending, int notVerified)
    {
        var row = Row("riot", association: association, matchStatus: matchStatus);
        var set = Build([row]);

        set.Games.Single().Tier.Should().Be(DataTier.ScoreOnly);
        (set.Coverage.RiotPending, set.Coverage.RiotNotVerified).Should().Be((pending, notVerified));
    }

    [Fact]
    public void A_verified_association_without_retrieved_data_waits_rather_than_inventing_detail()
    {
        var row = Row("riot", association: "verified", matchStatus: "pending");
        var set = Build([row]);
        set.Games.Single().Tier.Should().Be(DataTier.ScoreOnly);
        set.Coverage.RiotPending.Should().Be(1);
    }

    [Fact]
    public void Result_follows_the_winner_slot_then_the_score()
    {
        ScrimDataBuilder.Result(Row(s1: 13, s2: 7, slot: 1)).Should().Be("win");
        ScrimDataBuilder.Result(Row(s1: 13, s2: 7, slot: 2)).Should().Be("loss");
        ScrimDataBuilder.Result(Row(s1: 7, s2: 13, slot: 2, winner: 2)).Should().Be("win");
        ScrimDataBuilder.Result(Row(s1: 12, s2: 12, slot: 1) with { WinnerSlot = null }).Should().Be("draw");
    }

    [Fact]
    public void Coverage_counts_each_tier_and_passes_on_what_is_not_counted_yet()
    {
        var (riotRow, games, players) = RiotMap();
        var set = Build([riotRow, Row("ocr", ocr: Ocr(Player(true, "A", "x"))), Row(), Row()], games, players, awaiting: 2, disputed: 1);

        set.Coverage.Should().Be(new Coverage(4, 1, 1, 2, 0, 0, 2, 1));
    }

    [Fact]
    public void The_same_map_is_one_map_whatever_the_source_called_it()
    {
        ScrimDataBuilder.CanonicalMap("ICEBOX", null).Should().Be("Icebox");
        ScrimDataBuilder.CanonicalMap("  Bind ", null).Should().Be("Bind");
        ScrimDataBuilder.CanonicalMap("Bind", "/Game/Maps/Duality/Duality").Should().Be("Bind");
        ScrimDataBuilder.CanonicalMap("  ", null).Should().BeNull();
        ScrimDataBuilder.CanonicalMap("Some New Map", null).Should().Be("Some New Map");
    }
}
