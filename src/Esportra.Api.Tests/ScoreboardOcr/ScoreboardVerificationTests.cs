using System.Text.Json;
using Esportra.Api.ScoreboardOcr;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.ScoreboardOcr;

public sealed class ScoreboardVerificationTests
{
    private static readonly OcrRosters Rosters = new(
        [
            new OcrRosterPlayer("u1", [new OcrRosterName("L1te", OcrNameSource.RiotVerified), new OcrRosterName("lite_es", OcrNameSource.Username)]),
            new OcrRosterPlayer("u2", [new OcrRosterName("Kaidn", OcrNameSource.RiotClaimed)]),
            new OcrRosterPlayer("u3", [new OcrRosterName("سعد", OcrNameSource.RiotVerified)]),
        ],
        [new OcrRosterPlayer("u9", [new OcrRosterName("Rivers of Blood", OcrNameSource.RiotVerified)])]);

    private static JsonElement Result(params object[] players) =>
        JsonSerializer.SerializeToElement(new { allyTeam = new { value = "team1" }, players });

    private static object Row(
        string side, string? read, string? userId = null, string? team = null, string? source = null, string method = "read", string? shown = null) =>
        new
        {
            side,
            name = new { value = shown ?? read },
            nameRead = read,
            rosterMatch = userId is null ? null : new { userId, team, source, method },
        };

    [Fact]
    public void FromParse_ClassifiesEachRowBySource()
    {
        var verification = ScoreboardVerification.FromParse(
            Result(
                Row("ally", "L 1 t e", "u1", "team1", OcrNameSource.RiotVerified),
                Row("ally", "Kaidn", "u2", "team1", OcrNameSource.RiotClaimed),
                Row("ally", null, "u3", "team1", OcrNameSource.RiotVerified, "elimination", shown: "سعد"),
                Row("ally", "Ringer")),
            Rosters);

        verification.Players.Select(p => p.Status).Should().Equal(
            VerificationStatus.Verified, VerificationStatus.Claimed, VerificationStatus.Unconfirmed, VerificationStatus.Unknown);
        verification.NeedsReview.Should().ContainSingle(p => p.Name == "Ringer");
    }

    [Fact]
    public void Verify_PlayerOnTheOpponentsRoster_IsOtherTeam()
    {
        var verification = ScoreboardVerification.FromParse(Result(Row("ally", "Rivers of Blood", "u9", "team2", OcrNameSource.RiotVerified)), Rosters);

        verification.Players.Single().Status.Should().Be(VerificationStatus.OtherTeam);
    }

    [Fact]
    public void Verify_CaptainRenamingAnUnknownRowToARosterName_IsEdited()
    {
        var result = Result(Row("ally", "Ringer"));

        var verification = ScoreboardVerification.Verify(result, [new SubmittedPlayer("Kaidn", "team1")], Rosters);

        var player = verification.Players.Single();
        player.Status.Should().Be(VerificationStatus.Edited);
        player.ReadName.Should().Be("Ringer");
        player.UserId.Should().Be("u2");
    }

    [Fact]
    public void Verify_KeepingTheRosterSpellingOfAMatchedRow_StaysVerified()
    {
        var result = Result(Row("ally", "L 1te", "u1", "team1", OcrNameSource.RiotVerified));

        var verification = ScoreboardVerification.Verify(result, [new SubmittedPlayer("L1te", "team1")], Rosters);

        verification.Players.Single().Status.Should().Be(VerificationStatus.Verified);
    }

    [Fact]
    public void Verify_SamePlayerTwice_IsDuplicate()
    {
        var result = Result(Row("ally", "Kaidn", "u2", "team1", OcrNameSource.RiotClaimed), Row("ally", "Somebody"));

        var verification = ScoreboardVerification.Verify(result, [new SubmittedPlayer("Kaidn", "team1"), new SubmittedPlayer("Kaidn", "team1")], Rosters);

        verification.Players[1].Status.Should().Be(VerificationStatus.Duplicate);
    }

    [Fact]
    public void Verify_MovingAPlayerToTheWrongTeam_IsOtherTeam()
    {
        var result = Result(Row("ally", "Kaidn", "u2", "team1", OcrNameSource.RiotClaimed));

        var verification = ScoreboardVerification.Verify(result, [new SubmittedPlayer("Kaidn", "team2")], Rosters);

        verification.Players.Single().Status.Should().Be(VerificationStatus.OtherTeam);
    }

    [Theory]
    [InlineData("L 1 t e", "L1te", true)]
    [InlineData("ZyWoO", "ZyWo0", true)]
    [InlineData("Kooltkk#EU1", "kooltkk", true)]
    [InlineData("كبده وصدوقها", "كبدهوصدوقها", true)]
    [InlineData("Nothing1", "Nothing2", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("Shoosh", "Saintt", false)]
    public void NameKey_MatchesLikeTheOcrService(string a, string b, bool close) =>
        NameKey.Close(NameKey.Of(a), NameKey.Of(b)).Should().Be(close);

    [Fact]
    public void OrganizerLines_DescribeEachFinding()
    {
        var lines = ScoreboardReportVerifier.Lines(
            "Alpha",
            [
                new VerifiedPlayer(0, "Ringer", "team1", null, VerificationStatus.Unknown, null, "Ringer"),
                new VerifiedPlayer(1, "Kaidn", "team1", "u2", VerificationStatus.Edited, null, "Kald"),
            ],
            [ScoreboardReportVerifier.FlagReused]).ToList();

        lines[0].Should().Be("Alpha reported a result from a screenshot.");
        lines.Should().Contain(l => l.Contains("Ringer isn't on either team's roster"));
        lines.Should().Contain(l => l.Contains("typed by the captain") && l.Contains("\"Kald\""));
        lines.Should().Contain(l => l.Contains("used for another match"));
    }
}
