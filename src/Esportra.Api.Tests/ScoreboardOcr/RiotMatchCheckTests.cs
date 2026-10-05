using System.Text.Json;
using Esportra.Api.ScoreboardOcr;
using Esportra.Api.Services;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.ScoreboardOcr;

/// <summary>Screenshot reports checked against Riot's record of the match (full Riot IDs, by PUUID).</summary>
public sealed class RiotMatchCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 20, 0, 0, TimeSpan.Zero);

    private static readonly LinkedAccount[] Linked =
    [
        new("cap1", "team1", "p-darkhyper"),
        new("mate1", "team1", "p-saya"),
        new("cap2", "team2", "p-saintt"),
    ];

    private static RiotMatch Match(params RiotMatchPlayer[] players) =>
        new("riot-1", "Sunset", "custom", 15, 13, Now, players);

    private static RiotMatchPlayer Riot(string puuid, string name, string tag, string side, int k = 10, int d = 10, int a = 5) =>
        new(puuid, name, tag, side, k, d, a);

    private static VerifiedPlayer Row(int index, string name, string? team, string? userId, string status = VerificationStatus.Verified) =>
        new(index, name, team, userId, status, OcrNameSource.RiotVerified, name);

    [Fact]
    public void LinkedPlayersWhoPlayed_AreConfirmedWithTheirFullRiotId()
    {
        var verification = new OcrVerification([Row(0, "DarkHyper", "team1", "cap1"), Row(1, "Saintt", "team2", "cap2")], []);
        var match = Match(Riot("p-darkhyper", "DarkHyper", "007", "Blue"), Riot("p-saintt", "Saintt", "wpp", "Red"));

        var result = ScoreboardVerification.ApplyRiotMatch(verification, Linked, match, [], (15, 13), Now);

        result.Players.Select(p => (p.RiotStatus, p.RiotId)).Should().Equal(
            (RiotStatus.Confirmed, "DarkHyper#007"), (RiotStatus.Confirmed, "Saintt#wpp"));
        result.Riot.Should().Be(new RiotCheck(RiotCheckStatus.Confirmed, "riot-1", Now));
        result.NeedsReview.Should().BeEmpty();
        result.Flags.Should().BeEmpty();
    }

    [Fact]
    public void SameGameNameOnAnotherAccount_IsFlagged()
    {
        var verification = new OcrVerification([Row(0, "DarkHyper", "team1", "cap1")], []);
        var match = Match(Riot("p-impostor", "DarkHyper", "999", "Blue"), Riot("p-saintt", "Saintt", "wpp", "Red"));

        var result = ScoreboardVerification.ApplyRiotMatch(verification, Linked, match, [], (15, 13), Now);

        var player = result.Players.Single();
        (player.RiotStatus, player.RiotId).Should().Be((RiotStatus.OtherAccount, "DarkHyper#999"));
        result.NeedsReview.Should().ContainSingle();
    }

    [Fact]
    public void LinkedPlayerAbsentFromTheMatch_IsNotInMatch()
    {
        var verification = new OcrVerification([Row(0, "Sayaplayer", "team1", "mate1")], []);

        var result = ScoreboardVerification.ApplyRiotMatch(
            verification, Linked, Match(Riot("p-darkhyper", "DarkHyper", "007", "Blue")), [], (15, 13), Now);

        result.Players.Single().RiotStatus.Should().Be(RiotStatus.NotInMatch);
    }

    [Fact]
    public void AccountNoOneLinked_IsUnregistered()
    {
        var verification = new OcrVerification([Row(0, "ghastii", "team2", null, VerificationStatus.Unknown)], []);

        var result = ScoreboardVerification.ApplyRiotMatch(
            verification, Linked, Match(Riot("p-ghastii", "ghastii", "EUW", "Red")), [], (15, 13), Now);

        var player = result.Players.Single();
        (player.RiotStatus, player.RiotId).Should().Be((RiotStatus.Unregistered, "ghastii#EUW"));
    }

    [Fact]
    public void RowTheScreenshotCouldNotPlace_IsResolvedByTheAccountRiotListed()
    {
        // The name didn't match the roster spelling, but Riot says it is cap2's linked account.
        var verification = new OcrVerification([Row(0, "Saintt", "team2", null, VerificationStatus.Unknown)], []);

        var result = ScoreboardVerification.ApplyRiotMatch(
            verification, Linked, Match(Riot("p-saintt", "Saintt", "wpp", "Red")), [], (15, 13), Now);

        var player = result.Players.Single();
        (player.UserId, player.Status, player.RiotStatus).Should().Be(("cap2", VerificationStatus.Verified, RiotStatus.Confirmed));
    }

    [Fact]
    public void EditedKdaAndScore_AreFlagged()
    {
        var verification = new OcrVerification([Row(0, "DarkHyper", "team1", "cap1"), Row(1, "Saintt", "team2", "cap2")], []);
        var match = Match(Riot("p-darkhyper", "DarkHyper", "007", "Blue", 19, 18, 7), Riot("p-saintt", "Saintt", "wpp", "Red", 26, 19, 17));
        SubmittedPlayer[] submitted = [new("DarkHyper", "team1", 25, 18, 7), new("Saintt", "team2", 26, 19, 17)];

        var result = ScoreboardVerification.ApplyRiotMatch(verification, Linked, match, submitted, (13, 15), Now);

        result.Flags.Should().BeEquivalentTo([ScoreboardVerification.FlagStatsMismatch, ScoreboardVerification.FlagScoreMismatch]);
        result.Players[0].RiotKda.Should().Be("19/18/7");
        result.Players[1].RiotKda.Should().BeNull();
    }

    [Fact]
    public void ScoreIsComparedFromEachTeamsSide()
    {
        // team1 played Red here: Riot's 15-13 for Blue is 13-15 for team1.
        var verification = new OcrVerification([Row(0, "DarkHyper", "team1", "cap1")], []);
        var match = Match(Riot("p-darkhyper", "DarkHyper", "007", "Red"), Riot("p-saintt", "Saintt", "wpp", "Blue"));

        ScoreboardVerification.ApplyRiotMatch(verification, Linked, match, [], (13, 15), Now).Flags.Should().BeEmpty();
        ScoreboardVerification.ApplyRiotMatch(verification, Linked, match, [], (15, 13), Now).Flags
            .Should().Contain(ScoreboardVerification.FlagScoreMismatch);
    }

    [Fact]
    public void StoredVerification_RoundTripsWithTheRiotCheck()
    {
        var verification = new OcrVerification(
            [Row(0, "DarkHyper", "team1", "cap1") with { RiotStatus = RiotStatus.Confirmed, RiotId = "DarkHyper#007" }],
            [], new RiotCheck(RiotCheckStatus.Confirmed, "riot-1", Now));

        var json = JsonSerializer.Serialize(verification, OcrVerificationJson.Options);
        var back = JsonSerializer.Deserialize<OcrVerification>(json, OcrVerificationJson.Options)!;

        json.Should().Contain("\"riotId\":\"DarkHyper#007\"").And.Contain("\"riot\":{\"status\":\"confirmed\"");
        back.Players.Single().Should().Be(verification.Players.Single());
        back.Riot.Should().Be(verification.Riot);
    }

    [Fact]
    public void Matchlist_KeepsCustomGamesInTheWindow_ClosestFirst()
    {
        var at = Now.ToUnixTimeMilliseconds();
        var list = JsonSerializer.Serialize(new
        {
            history = new object[]
            {
                new { matchId = "ranked", gameStartTimeMillis = at, queueId = "competitive" },
                new { matchId = "far", gameStartTimeMillis = at - (long)TimeSpan.FromDays(5).TotalMilliseconds, queueId = "custom" },
                new { matchId = "later", gameStartTimeMillis = at + (long)TimeSpan.FromHours(6).TotalMilliseconds, queueId = "custom" },
                new { matchId = "closest", gameStartTimeMillis = at - (long)TimeSpan.FromHours(1).TotalMilliseconds, queueId = "custom" },
            },
        });

        RiotMatchLookup.CandidateIds(list, new RiotMatchQuery("Sunset", 15, 13, Now, TimeSpan.FromDays(2)))
            .Should().Equal("closest", "later");
    }

    [Fact]
    public void MatchDetails_AreParsedAndFittedByMapAndScore()
    {
        var detail = JsonSerializer.SerializeToElement(new
        {
            matchInfo = new { matchId = "riot-1", mapId = "/Game/Maps/Juliett/Juliett", queueId = "custom", gameStartMillis = Now.ToUnixTimeMilliseconds() },
            teams = new object[] { new { teamId = "Blue", roundsWon = 13 }, new { teamId = "Red", roundsWon = 15 } },
            players = new object[]
            {
                new { puuid = "p-saintt", gameName = "Saintt", tagLine = "wpp", teamId = "Red", stats = new { kills = 26, deaths = 19, assists = 17 } },
            },
        });

        var match = RiotMatchLookup.ParseMatch(detail);

        match.Map.Should().Be("Sunset");
        match.Players.Single().Should().Be(new RiotMatchPlayer("p-saintt", "Saintt", "wpp", "Red", 26, 19, 17));
        RiotMatchLookup.Fits(match, new RiotMatchQuery("Sunset", 15, 13, Now, TimeSpan.FromDays(2))).Should().BeTrue();
        RiotMatchLookup.Fits(match, new RiotMatchQuery("Lotus", 15, 13, Now, TimeSpan.FromDays(2))).Should().BeFalse();
        RiotMatchLookup.Fits(match, new RiotMatchQuery("Sunset", 13, 11, Now, TimeSpan.FromDays(2))).Should().BeFalse();
    }

    [Fact]
    public void FullyConfirmed_OnlyWhenRiotBacksEveryRowAndNumber()
    {
        var match = Match(Riot("p-darkhyper", "DarkHyper", "007", "Blue"), Riot("p-saintt", "Saintt", "wpp", "Red"));
        var rows = new OcrVerification([Row(0, "DarkHyper", "team1", "cap1"), Row(1, "Saintt", "team2", "cap2")], []);
        var confirmed = ScoreboardVerification.ApplyRiotMatch(rows, Linked, match, [], (15, 13), Now);

        ScoreboardVerification.IsFullyConfirmed(confirmed, match, Linked).Should().BeTrue();
        ScoreboardVerification.IsFullyConfirmed(confirmed with { Flags = [ScoreboardVerification.FlagStatsMismatch] }, match, Linked).Should().BeFalse();
        ScoreboardVerification.IsFullyConfirmed(confirmed with { Players = confirmed.Players.Take(1).ToList() }, match, Linked).Should().BeFalse();
        ScoreboardVerification.IsFullyConfirmed(rows, match, Linked).Should().BeFalse("Riot hasn't been checked");
        var withImpostor = Match(Riot("p-darkhyper", "DarkHyper", "007", "Blue"), Riot("p-impostor", "Saintt", "999", "Red"));
        ScoreboardVerification.IsFullyConfirmed(
            ScoreboardVerification.ApplyRiotMatch(rows, Linked, withImpostor, [], (15, 13), Now), withImpostor, Linked).Should().BeFalse();
    }

    [Fact]
    public void ReviewPayload_GivesRoundsPerBracketTeam_AndTeamsUnlinkedTeammates()
    {
        var match = RiotMatchSamples.Match("riot-2", "Juliett", 13, 15, Now,
            new RiotMatchSamples.Player("p-saintt", "Saintt", "wpp", "Blue"), new RiotMatchSamples.Player("p-mate", "Mate", "BBB", "Blue"), new RiotMatchSamples.Player("p-darkhyper", "DarkHyper", "007", "Red"));

        var payload = JsonSerializer.SerializeToElement(ScoreboardRiotMatcher.Payload(match, Linked));

        (payload.GetProperty("team1Rounds").GetInt32(), payload.GetProperty("team2Rounds").GetInt32()).Should().Be((15, 13));
        payload.GetProperty("map").GetString().Should().Be("Sunset");
        var mate = payload.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("gameName").GetString() == "Mate");
        (mate.GetProperty("isTeam2").GetBoolean(), mate.GetProperty("acs").GetInt32()).Should().Be((true, 4200 / 28));
    }

    private static readonly string[] AllyNames = ["Saintt", "Sayaplayer", "DarkHyper", "Time in a Bottle", "Aim Not Found"];
    private static readonly string[] EnemyNames = ["Paincake", "psycho", "ghastii", "شاي حبق", "CBlEctoplasm365"];

    /// <summary>The user's Sunset 15-13: allies on Blue.</summary>
    private static RiotMatch Sunset(string allySide = "Blue") =>
        RiotMatchSamples.Match("riot-sunset", "Juliett", allySide == "Blue" ? 15 : 13, allySide == "Blue" ? 13 : 15, Now,
            [.. AllyNames.Select(n => new RiotMatchSamples.Player($"p-{n}", n, "TAG", allySide)),
             .. EnemyNames.Select(n => new RiotMatchSamples.Player($"p-{n}", n, "TAG", allySide == "Blue" ? "Red" : "Blue"))]);

    private static JsonElement Read(string map = "Sunset", int ally = 15, int enemy = 13, IEnumerable<(string? Name, string Side)>? rows = null) =>
        JsonSerializer.SerializeToElement(new
        {
            map = new { value = new { name = map } },
            allyScore = new { value = ally },
            enemyScore = new { value = enemy },
            allyTeam = new { value = "team1" },
            players = (rows ?? AllyNames.Select(n => ((string?)n, "ally")).Concat(EnemyNames.Select(n => ((string?)n, "enemy"))))
                .Select(r => new { side = r.Side, nameRead = r.Name, name = new { value = r.Name } }),
        });

    [Fact]
    public void SameGame_WhenMapScoreSidesAndEveryNameMatch()
    {
        ScoreboardRiotMatcher.SameGameAllySide(Read(), Sunset()).Should().Be("Blue");
        ScoreboardRiotMatcher.SameGameAllySide(Read(), Sunset(allySide: "Red")).Should().Be("Red", "the screenshot owner's team may have played Red");
    }

    [Fact]
    public void SameGame_ToleratesOcrLookAlikesAndUnreadableNames()
    {
        // "CBIEctoplasm365" read with a capital I, and the Arabic name unreadable without the model.
        var rows = AllyNames.Select(n => ((string?)n, "ally"))
            .Concat(EnemyNames.Select(n => (n == "شاي حبق" ? null : n == "CBlEctoplasm365" ? "CBIEctoplasm365" : (string?)n, "enemy")));

        ScoreboardRiotMatcher.SameGameAllySide(Read(rows: rows), Sunset()).Should().Be("Blue");
    }

    [Fact]
    public void NotTheSameGame_WhenAnythingDisagrees()
    {
        ScoreboardRiotMatcher.SameGameAllySide(Read(map: "Lotus"), Sunset()).Should().BeNull();
        ScoreboardRiotMatcher.SameGameAllySide(Read(ally: 13, enemy: 15), Sunset()).Should().BeNull();
        var stranger = AllyNames.Select(n => ((string?)(n == "psycho" ? "psycho" : n), "ally")).ToList();
        stranger[0] = ("Somebody Else", "ally");
        ScoreboardRiotMatcher.SameGameAllySide(Read(rows: stranger.Concat(EnemyNames.Select(n => ((string?)n, "enemy")))), Sunset())
            .Should().BeNull("a name the game doesn't have means it's another game");
        var swapped = AllyNames.Select(n => ((string?)n, "enemy")).Concat(EnemyNames.Select(n => ((string?)n, "ally")));
        ScoreboardRiotMatcher.SameGameAllySide(Read(rows: swapped), Sunset()).Should().BeNull("the score is from the other side");
        var tooFew = AllyNames.Take(3).Select(n => ((string?)n, "ally")).Concat(EnemyNames.Take(3).Select(n => ((string?)n, "enemy")));
        ScoreboardRiotMatcher.SameGameAllySide(Read(rows: tooFew), Sunset()).Should().BeNull();
    }

    [Fact]
    public void Candidate_IsTheAutoFetchShape_FromTheReportingTeam()
    {
        LinkedAccount[] linked = [new("u-saintt", "team1", "p-Saintt")];

        var candidate = JsonSerializer.SerializeToElement(ScoreboardRiotMatcher.Candidate(Sunset(), "Blue", "team1", "team2", linked));

        candidate.GetProperty("id").GetString().Should().Be("riot-sunset");
        candidate.GetProperty("reporterSide").GetString().Should().Be("Red");
        (candidate.GetProperty("myTeamScore").GetInt32(), candidate.GetProperty("enemyTeamScore").GetInt32()).Should().Be((13, 15));
        candidate.GetProperty("result").GetString().Should().Be("Defeat");
        var ghastii = candidate.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("gameName").GetString() == "ghastii");
        (ghastii.GetProperty("isTeam2").GetBoolean(), ghastii.GetProperty("tagLine").GetString()).Should().Be((true, "TAG"));
    }

    [Fact]
    public void RiotSourcedCheck_HasOneRowPerRiotPlayer()
    {
        LinkedAccount[] linked = [new("cap1", "team1", "p-Saintt"), new("cap2", "team2", "p-ghastii")];

        var check = ScoreboardVerification.FromRiotMatch(Sunset(), linked, null);

        check.Players.Should().HaveCount(10);
        check.Players.Single(p => p.Name == "Saintt").Should().Match<VerifiedPlayer>(p => p.UserId == "cap1" && p.Team == "team1" && p.Status == VerificationStatus.Verified);
        check.Players.Single(p => p.Name == "psycho").Should().Match<VerifiedPlayer>(p => p.UserId == null && p.Team == "team2" && p.Status == VerificationStatus.Unknown);
    }
}
