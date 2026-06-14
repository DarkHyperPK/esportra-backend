using Esportra.Core.Tournaments;
using Xunit;

namespace Esportra.Core.Tests.Tournaments;

public sealed class CheckinWalkoverProcessorTests
{
    private static SelfPlayMatchRoomContext BaseContext(
        DateTime? scheduledTime = null,
        bool team1CheckedIn = false,
        bool team2CheckedIn = false,
        string status = "pending",
        bool selfPlayEnabled = true,
        DateTime? acceptedProposalTime = null)
    {
        return new SelfPlayMatchRoomContext
        {
            MatchId = Guid.NewGuid(),
            Status = status,
            MatchScheduledTime = scheduledTime,
            AcceptedProposalTime = acceptedProposalTime ?? scheduledTime,
            Team1Id = Guid.NewGuid(),
            Team2Id = Guid.NewGuid(),
            Team1CheckedIn = team1CheckedIn,
            Team2CheckedIn = team2CheckedIn,
            SchedulingConfig = new SchedulingConfigSnapshot(
                SelfPlayEnabled: selfPlayEnabled,
                CheckinWindowMinutes: 15,
                RoundDeadlines: new Dictionary<string, string>()),
            Game = "Counter-Strike 2",
        };
    }

    [Fact]
    public void EvaluateEligibility_before_window_closes_is_not_due()
    {
        var scheduled = DateTime.UtcNow.AddMinutes(5);
        var ctx = BaseContext(scheduledTime: scheduled);

        var outcome = CheckinWalkoverProcessor.EvaluateEligibility(ctx, DateTime.UtcNow);

        Assert.Equal(CheckinWalkoverStatus.WindowNotClosed, outcome.Status);
        Assert.False(outcome.Processed);
    }

    [Fact]
    public void EvaluateEligibility_after_window_one_checked_in_awards_walkover()
    {
        var scheduled = DateTime.UtcNow.AddHours(-2);
        var ctx = BaseContext(scheduledTime: scheduled, team1CheckedIn: true);

        var outcome = CheckinWalkoverProcessor.EvaluateEligibility(ctx, DateTime.UtcNow);

        Assert.Equal(CheckinWalkoverStatus.WalkoverAwarded, outcome.Status);
        Assert.Equal(ctx.Team1Id, outcome.WinnerId);
        Assert.True(outcome.Team1CheckedIn);
        Assert.False(outcome.Team2CheckedIn);
    }

    [Fact]
    public void EvaluateEligibility_after_window_neither_checked_in_stays_pending()
    {
        var scheduled = DateTime.UtcNow.AddHours(-2);
        var ctx = BaseContext(scheduledTime: scheduled);

        var outcome = CheckinWalkoverProcessor.EvaluateEligibility(ctx, DateTime.UtcNow);

        Assert.Equal(CheckinWalkoverStatus.WindowNotClosed, outcome.Status);
        Assert.Null(outcome.WinnerId);
        Assert.False(outcome.Processed);
    }

    [Fact]
    public void EvaluateEligibility_self_play_without_agreed_time_not_due()
    {
        var scheduled = DateTime.UtcNow.AddHours(-2);
        var ctx = BaseContext(scheduledTime: scheduled) with { AcceptedProposalTime = null };

        var outcome = CheckinWalkoverProcessor.EvaluateEligibility(ctx, DateTime.UtcNow);

        Assert.Equal(CheckinWalkoverStatus.WindowNotClosed, outcome.Status);
    }

    [Fact]
    public void EvaluateEligibility_non_self_play_uses_organizer_schedule()
    {
        var scheduled = DateTime.UtcNow.AddHours(-2);
        var ctx = BaseContext(scheduledTime: scheduled, selfPlayEnabled: false, acceptedProposalTime: null);

        var outcome = CheckinWalkoverProcessor.EvaluateEligibility(ctx, DateTime.UtcNow);

        Assert.Equal(CheckinWalkoverStatus.WindowNotClosed, outcome.Status);
        Assert.False(outcome.Processed);
    }

    [Fact]
    public void EvaluateEligibility_at_scheduled_time_one_checked_in_awards_walkover()
    {
        var scheduled = DateTime.UtcNow;
        var ctx = BaseContext(scheduledTime: scheduled, team1CheckedIn: true);

        var outcome = CheckinWalkoverProcessor.EvaluateEligibility(ctx, scheduled);

        Assert.Equal(CheckinWalkoverStatus.WalkoverAwarded, outcome.Status);
    }

    [Fact]
    public void EvaluateEligibility_completed_match_not_applicable()
    {
        var scheduled = DateTime.UtcNow.AddHours(-2);
        var ctx = BaseContext(scheduledTime: scheduled, status: "completed");

        var outcome = CheckinWalkoverProcessor.EvaluateEligibility(ctx, DateTime.UtcNow);

        Assert.Equal(CheckinWalkoverStatus.NotApplicable, outcome.Status);
    }
}
