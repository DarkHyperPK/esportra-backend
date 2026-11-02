using Esportra.Api.Services;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

public sealed class ScrimSchedulingTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    // ── What accepting an offer does ────────────────────────────────────────────

    [Fact]
    public void Accept_WithoutATime_LeavesTheMatchUnscheduled()
    {
        var plan = ScrimAcceptPlan.For(slotId: null, acceptBodyTime: null, offerProposedAt: null, Now);
        plan.ScheduledAt.Should().BeNull();
        plan.InitialProposalAt.Should().BeNull();
    }

    [Fact]
    public void Accept_TurnsTheOfferersSuggestedTime_IntoAProposalToConfirm_NotAFixedTime()
    {
        var suggested = Now.AddDays(1);
        var plan = ScrimAcceptPlan.For(null, null, suggested, Now);
        plan.ScheduledAt.Should().BeNull("accepting must not lock in a time the accepting captain never confirmed");
        plan.InitialProposalAt.Should().Be(suggested);
    }

    [Fact]
    public void Accept_IgnoresASuggestedTimeThatIsAlreadyPastOrTooSoon()
    {
        // proposed_at defaults to now() when the offerer picked nothing, so near-now means "no suggestion".
        ScrimAcceptPlan.For(null, null, Now.AddMinutes(-5), Now).InitialProposalAt.Should().BeNull();
        ScrimAcceptPlan.For(null, null, Now.AddMinutes(2), Now).InitialProposalAt.Should().BeNull();
        ScrimAcceptPlan.For(null, null, Now.AddMinutes(ScrimScheduling.MinLeadMinutes), Now).InitialProposalAt.Should().NotBeNull();
    }

    [Fact]
    public void Accept_OfASlotOffer_SchedulesAtTheSlot_BecauseTheOwnerPublishedIt()
    {
        var slotStart = Now.AddDays(2);
        var plan = ScrimAcceptPlan.For(Guid.NewGuid(), null, slotStart, Now);
        plan.ScheduledAt.Should().Be(slotStart);
        plan.InitialProposalAt.Should().BeNull();
    }

    [Fact]
    public void Accept_WithAnExplicitTime_StillHonoursIt_ForOlderClients()
    {
        var explicitTime = Now.AddDays(3);
        var plan = ScrimAcceptPlan.For(null, explicitTime, Now.AddDays(1), Now);
        plan.ScheduledAt.Should().Be(explicitTime);
        plan.InitialProposalAt.Should().BeNull();
    }

    // ── Proposed times ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-10, false)]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(60 * 24 * 59, true)]
    [InlineData(60 * 24 * 61, false)]
    public void ProposedTime_MustBeSoonButNotTooSoon_AndNotMonthsAway(int minutesFromNow, bool valid)
    {
        (ScrimScheduling.ValidateProposedTime(Now.AddMinutes(minutesFromNow), Now) is null).Should().Be(valid);
    }

    [Fact]
    public void ProposedTimeErrors_SayWhatToDo()
    {
        ScrimScheduling.ValidateProposedTime(Now.AddMinutes(1), Now).Should().Contain("at least");
        ScrimScheduling.ValidateProposedTime(Now.AddDays(90), Now).Should().Contain("60 days");
    }

    // ── Check-in automation follows the agreed time ─────────────────────────────

    [Fact]
    public void JobTimes_AreReminder30Before_Window15Before_AndTimeout15After()
    {
        var start = new DateTime(2026, 11, 5, 20, 0, 0, DateTimeKind.Utc);
        var t = ScrimScheduling.JobTimes(start);
        t.Reminder.Should().Be(start.AddMinutes(-30));
        t.Window.Should().Be(start.AddMinutes(-15));
        t.Timeout.Should().Be(start.AddMinutes(15));
    }
}
