using Esportra.Api.Services;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

public sealed class ScrimUpNextBuilderTests
{
    private static readonly Guid Me = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Them = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly DateTime Start = new(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlySet<Guid> Mine = new HashSet<Guid> { Me };

    private static UpNextSessionRow S(Func<UpNextSessionRow, UpNextSessionRow> tweak) => tweak(new UpNextSessionRow
    {
        Id = Guid.NewGuid(),
        Team1Id = Me,
        Team2Id = Them,
        Team1Name = "Me",
        Team2Name = "Them",
        Status = "scheduled",
        ScheduledAt = Start,
        CheckinWindowOpenedAt = Start.AddMinutes(-15),
    });

    private static string[] Types(params UpNextSessionRow[] sessions) =>
        ScrimUpNextBuilder.Build([], sessions, Mine, Start).Items.Select(i => i.Type).ToArray();

    [Theory]
    [InlineData(-16, false)]
    [InlineData(-15, true)]
    [InlineData(0, true)]
    [InlineData(15, true)]
    [InlineData(16, false)]
    public void CheckinOpen_FollowsTheT15Window(int minutesFromStart, bool expected)
    {
        var now = Start.AddMinutes(minutesFromStart);
        var items = ScrimUpNextBuilder.Build([], [S(s => s)], Mine, now).Items;
        items.Any(i => i.Type == "checkin_open").Should().Be(expected);
    }

    [Fact]
    public void CheckinOpen_IsNotOffered_UntilTheServerHasOpenedTheWindow()
    {
        // Inside T-15..T+15 by the clock, but the window job has not run: check-in would be rejected.
        Types(S(s => s with { CheckinWindowOpenedAt = null })).Should().BeEmpty();
    }

    [Fact]
    public void CheckinOpen_HasDeadlineAtWindowClose_AndClearsOnceCheckedIn()
    {
        var item = ScrimUpNextBuilder.Build([], [S(s => s)], Mine, Start).Items.Single();
        item.Deadline.Should().Be(Start.AddMinutes(15));
        item.OpponentName.Should().Be("Them");

        Types(S(s => s with { Team1CheckedInAt = Start })).Should().BeEmpty();
    }

    [Fact]
    public void CheckinOpen_UsesTheCallersSide()
    {
        // Caller is team2; only team2's check-in matters.
        var row = S(s => s with { Team1Id = Them, Team2Id = Me, Team1Name = "Them", Team2Name = "Me", Team1CheckedInAt = Start });
        var item = ScrimUpNextBuilder.Build([], [row], Mine, Start).Items.Single();
        item.TeamId.Should().Be(Me);
        item.OpponentName.Should().Be("Them");
    }

    [Fact]
    public void NeverIncludesSessionsOfOtherTeams()
    {
        var stranger = Guid.NewGuid();
        var row = S(s => s with { Team1Id = stranger, Team2Id = Them });
        Types(row).Should().BeEmpty();
    }

    [Fact]
    public void Live_AsksToConfirm_WhenOpponentReportedFirst()
    {
        Types(S(s => s with { Status = "in_progress", ResultReportedByTeam2 = true })).Should().Equal("result_to_confirm");
    }

    [Fact]
    public void Live_AsksToReport_WhenNobodyReported_AndNothingAfterIReported()
    {
        Types(S(s => s with { Status = "in_progress" })).Should().Equal("result_to_report");
        Types(S(s => s with { Status = "in_progress", ResultReportedByTeam1 = true })).Should().BeEmpty();
    }

    [Fact]
    public void Live_PrefersVeto_WhenVetoIsOpen()
    {
        Types(S(s => s with { Status = "in_progress", VetoOpen = true })).Should().Equal("veto_ready");
    }

    [Fact]
    public void Live_StopsNaggingAboutOldUnreportedResults()
    {
        var old = S(s => s with { Status = "in_progress", ScheduledAt = Start.AddHours(-(ScrimUpNextBuilder.ReportReminderHours + 1)) });
        Types(old).Should().BeEmpty();
    }

    [Fact]
    public void Dispute_AsksResponderForEvidence_ButNotTheRaiser_OrOnceSubmitted()
    {
        UpNextSessionRow D(Guid raisedBy, int evidence) => S(s => s with
        {
            Status = "disputed",
            DisputeStatus = "open",
            DisputeRaisedByTeamId = raisedBy,
            DisputeResponderEvidenceCount = evidence,
        });

        Types(D(Them, 0)).Should().Equal("evidence_due");
        Types(D(Me, 0)).Should().BeEmpty();
        Types(D(Them, 2)).Should().BeEmpty();
        Types(S(s => s with { Status = "disputed", DisputeStatus = "resolved", DisputeRaisedByTeamId = Them })).Should().BeEmpty();
    }

    [Fact]
    public void Offers_OnlyForMyListings_WithA48HourDeadline_AndNoExpiredOnes()
    {
        UpNextOfferRow Offer(Guid listingTeam, DateTime created) => new()
        {
            Id = Guid.NewGuid(),
            ListingId = Guid.NewGuid(),
            ListingTeamId = listingTeam,
            OfferingTeamId = Them,
            OfferingTeamName = "Them",
            CreatedAt = created,
            ProposedAt = Start,
        };

        var fresh = Offer(Me, Start.AddHours(-1));
        var expired = Offer(Me, Start.AddHours(-49));
        var notMine = Offer(Guid.NewGuid(), Start.AddHours(-1));

        var items = ScrimUpNextBuilder.Build([fresh, expired, notMine], [], Mine, Start).Items;

        items.Should().ContainSingle();
        items[0].Type.Should().Be("offer_received");
        items[0].Deadline.Should().Be(fresh.CreatedAt.AddHours(48));
    }

    [Fact]
    public void Ordering_PutsTimeCriticalFirst_ThenByDeadline()
    {
        var offer = new UpNextOfferRow { Id = Guid.NewGuid(), ListingId = Guid.NewGuid(), ListingTeamId = Me, CreatedAt = Start.AddHours(-1), ProposedAt = Start };
        var checkin = S(s => s);
        var confirm = S(s => s with { Id = Guid.NewGuid(), Status = "in_progress", ResultReportedByTeam2 = true });

        var types = ScrimUpNextBuilder.Build([offer], [confirm, checkin], Mine, Start).Items.Select(i => i.Type);
        types.Should().Equal("checkin_open", "result_to_confirm", "offer_received");
    }

    [Fact]
    public void Caps_ItemsButReportsTheTotal()
    {
        var offers = Enumerable.Range(0, ScrimUpNextBuilder.MaxItems + 5).Select(i => new UpNextOfferRow
        {
            Id = Guid.NewGuid(),
            ListingId = Guid.NewGuid(),
            ListingTeamId = Me,
            CreatedAt = Start.AddMinutes(-i),
            ProposedAt = Start,
        });
        var result = ScrimUpNextBuilder.Build(offers, [], Mine, Start);
        result.Items.Should().HaveCount(ScrimUpNextBuilder.MaxItems);
        result.Total.Should().Be(ScrimUpNextBuilder.MaxItems + 5);
    }
}
