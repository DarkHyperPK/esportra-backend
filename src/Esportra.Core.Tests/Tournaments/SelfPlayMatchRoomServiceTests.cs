using System.Text.Json;
using Esportra.Core.Tournaments;
using Xunit;

namespace Esportra.Core.Tests.Tournaments;

public sealed class SchedulingConfigParserTests
{
    [Fact]
    public void Parse_snake_case_config()
    {
        var config = SchedulingConfigParser.Parse("""
            {"self_play_enabled":true,"checkin_window_minutes":20,"round_deadlines":{"0":"2026-06-10T18:00:00Z"}}
            """);

        Assert.True(config.SelfPlayEnabled);
        Assert.Equal(20, config.CheckinWindowMinutes);
        Assert.Equal("2026-06-10T18:00:00Z", config.RoundDeadlines["0"]);
    }

    [Fact]
    public void Parse_camelCase_config()
    {
        var config = SchedulingConfigParser.Parse("""
            {"selfPlayEnabled":true,"checkinWindowMinutes":30}
            """);

        Assert.True(config.SelfPlayEnabled);
        Assert.Equal(30, config.CheckinWindowMinutes);
    }

    [Fact]
    public void Parse_CheckinWindowMinutes_PascalCase()
    {
        var config = SchedulingConfigParser.Parse("""{"CheckinWindowMinutes":25}""");
        Assert.Equal(25, config.CheckinWindowMinutes);
    }

    [Fact]
    public void Parse_invalid_json_defaults()
    {
        var config = SchedulingConfigParser.Parse("not-json");
        Assert.False(config.SelfPlayEnabled);
        Assert.Equal(15, config.CheckinWindowMinutes);
        Assert.Empty(config.RoundDeadlines);
    }
}

public sealed class SelfPlayMatchRoomServiceTests
{
    private static SelfPlayMatchRoomContext BaseContext(
        string status = "pending",
        DateTime? scheduledTime = null,
        bool team1CheckedIn = false,
        bool team2CheckedIn = false,
        int roundIndex = 0,
        DateTime? tournamentStart = null,
        DateTime? acceptedProposalTime = null,
        bool agreedSchedule = true,
        string? vetoStatus = null,
        bool mapVetoEnabled = false)
    {
        return new SelfPlayMatchRoomContext
        {
            MatchId = Guid.NewGuid(),
            TournamentId = Guid.NewGuid(),
            Status = status,
            MatchScheduledTime = scheduledTime,
            AcceptedProposalTime = acceptedProposalTime
                ?? (agreedSchedule ? scheduledTime : null),
            RoundIndex = roundIndex,
            TournamentStartDate = tournamentStart,
            Team1Id = Guid.NewGuid(),
            Team2Id = Guid.NewGuid(),
            Team1CheckedIn = team1CheckedIn,
            Team2CheckedIn = team2CheckedIn,
            SchedulingConfig = new SchedulingConfigSnapshot(
                SelfPlayEnabled: true,
                CheckinWindowMinutes: 15,
                RoundDeadlines: new Dictionary<string, string>()),
            VetoStatus = vetoStatus,
            MapVetoEnabled = mapVetoEnabled,
            Game = "Counter-Strike 2",
        };
    }

    [Fact]
    public void CalculatePhase_self_play_off_when_battle_royale()
    {
        var ctx = BaseContext(scheduledTime: DateTime.UtcNow);
        ctx = ctx with { Game = "Fortnite" };

        Assert.False(SelfPlayMatchRoomService.IsSelfPlayActive(ctx));
    }

    [Fact]
    public void CalculatePhase_pending_no_schedule_needs_schedule()
    {
        var ctx = BaseContext();
        var phase = SelfPlayMatchRoomService.CalculatePhase(ctx, null, false, false);
        Assert.Equal(SelfPlayPhase.NeedsSchedule, phase);
    }

    [Fact]
    public void ResolveEffectiveSchedule_self_play_ignores_organizer_and_tournament_times()
    {
        var start = new DateTime(2026, 6, 10, 16, 58, 0, DateTimeKind.Utc);
        var ctx = BaseContext(roundIndex: 0, scheduledTime: start, tournamentStart: start, agreedSchedule: false);
        var (time, source) = SelfPlayMatchRoomService.ResolveEffectiveSchedule(ctx);

        Assert.Null(time);
        Assert.Null(source);
    }

    [Fact]
    public void ResolveEffectiveSchedule_self_play_uses_accepted_proposal()
    {
        var agreed = new DateTime(2026, 6, 11, 20, 0, 0, DateTimeKind.Utc);
        var ctx = BaseContext(acceptedProposalTime: agreed);
        var (time, source) = SelfPlayMatchRoomService.ResolveEffectiveSchedule(ctx);

        Assert.Equal(agreed, time);
        Assert.Equal("accepted_proposal", source);
    }

    [Fact]
    public void ResolveEffectiveSchedule_self_play_organizer_override_wins_after_agreement()
    {
        var agreed = new DateTime(2026, 6, 11, 20, 0, 0, DateTimeKind.Utc);
        var overridden = new DateTime(2026, 6, 12, 15, 30, 0, DateTimeKind.Utc);
        var ctx = BaseContext(scheduledTime: overridden, acceptedProposalTime: agreed);
        var (time, source) = SelfPlayMatchRoomService.ResolveEffectiveSchedule(ctx);

        Assert.Equal(overridden, time);
        Assert.Equal("organizer_override", source);
    }

    [Fact]
    public void ResolveEffectiveSchedule_non_self_play_uses_organizer_schedule()
    {
        var scheduled = new DateTime(2026, 6, 10, 18, 0, 0, DateTimeKind.Utc);
        var ctx = BaseContext(scheduledTime: scheduled) with
        {
            SchedulingConfig = SchedulingConfigSnapshot.Default,
        };
        var (time, source) = SelfPlayMatchRoomService.ResolveEffectiveSchedule(ctx);

        Assert.Equal(scheduled, time);
        Assert.Equal("match_schedule", source);
    }

    [Fact]
    public void CalculatePhase_both_checked_in_pending_awaiting_party_code()
    {
        var scheduled = DateTime.UtcNow;
        var ctx = BaseContext(team1CheckedIn: true, team2CheckedIn: true, scheduledTime: scheduled);
        var phase = SelfPlayMatchRoomService.CalculatePhase(ctx, scheduled, true, false);
        Assert.Equal(SelfPlayPhase.AwaitingPartyCode, phase);
    }

    [Fact]
    public void CalculatePhase_in_progress_veto_incomplete()
    {
        var scheduled = DateTime.UtcNow;
        var ctx = BaseContext(status: "in_progress", scheduledTime: scheduled, mapVetoEnabled: true, vetoStatus: "in_progress");
        var phase = SelfPlayMatchRoomService.CalculatePhase(ctx, scheduled, true, false);
        Assert.Equal(SelfPlayPhase.AwaitingVeto, phase);
    }

    [Fact]
    public void CalculatePhase_in_progress_veto_complete_ready()
    {
        var scheduled = DateTime.UtcNow;
        var ctx = BaseContext(status: "in_progress", scheduledTime: scheduled, mapVetoEnabled: true, vetoStatus: "completed");
        var phase = SelfPlayMatchRoomService.CalculatePhase(ctx, scheduled, true, true);
        Assert.Equal(SelfPlayPhase.ReadyForMatch, phase);
    }

    [Fact]
    public void CalculatePhase_completed()
    {
        var scheduled = DateTime.UtcNow;
        var ctx = BaseContext(status: "completed", scheduledTime: scheduled);
        var phase = SelfPlayMatchRoomService.CalculatePhase(ctx, scheduled, true, true);
        Assert.Equal(SelfPlayPhase.Completed, phase);
    }

    [Fact]
    public void CanCheckIn_before_window_denied()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var scheduled = DateTime.UtcNow.AddHours(2);
        var ctx = BaseContext(scheduledTime: scheduled);

        var result = service.CanCheckIn(ctx, ctx.Team1Id!.Value, DateTime.UtcNow);

        Assert.False(result.Allowed);
        Assert.Equal("checkin_window_not_open", result.Code);
    }

    [Fact]
    public void CanCheckIn_during_window_allowed()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var scheduled = DateTime.UtcNow.AddMinutes(5);
        var ctx = BaseContext(scheduledTime: scheduled);

        var result = service.CanCheckIn(ctx, ctx.Team1Id!.Value, DateTime.UtcNow);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void CanCheckIn_after_window_denied()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var scheduled = DateTime.UtcNow.AddHours(-2);
        var ctx = BaseContext(scheduledTime: scheduled);

        var result = service.CanCheckIn(ctx, ctx.Team1Id!.Value, DateTime.UtcNow);

        Assert.False(result.Allowed);
        Assert.Equal("checkin_window_closed", result.Code);
    }

    [Fact]
    public void CanCaptainGoLive_before_both_checkins_denied()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var scheduled = DateTime.UtcNow;
        var ctx = BaseContext(scheduledTime: scheduled, team1CheckedIn: true, team2CheckedIn: false);

        var result = service.CanCaptainGoLive(ctx, ctx.Team1Id, "ABCDE", DateTime.UtcNow);

        Assert.False(result.Allowed);
        Assert.Equal("self_play_checkins_required", result.Code);
    }

    [Fact]
    public void CanCaptainGoLive_team2_denied()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var scheduled = DateTime.UtcNow;
        var ctx = BaseContext(scheduledTime: scheduled, team1CheckedIn: true, team2CheckedIn: true);

        var result = service.CanCaptainGoLive(ctx, ctx.Team2Id, "ABCDE", DateTime.UtcNow);

        Assert.False(result.Allowed);
        Assert.Equal("team1_captain_required", result.Code);
    }

    [Fact]
    public void CanCaptainGoLive_without_code_denied()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var scheduled = DateTime.UtcNow;
        var ctx = BaseContext(scheduledTime: scheduled, team1CheckedIn: true, team2CheckedIn: true);

        var result = service.CanCaptainGoLive(ctx, ctx.Team1Id, "  ", DateTime.UtcNow);

        Assert.False(result.Allowed);
        Assert.Equal("party_code_required", result.Code);
    }

    [Fact]
    public void CanCaptainGoLive_after_both_checkins_allowed()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var scheduled = DateTime.UtcNow;
        var ctx = BaseContext(scheduledTime: scheduled, team1CheckedIn: true, team2CheckedIn: true);

        var result = service.CanCaptainGoLive(ctx, ctx.Team1Id, "ABCDE", DateTime.UtcNow);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void CanCaptainGoLive_too_early_denied()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var scheduled = DateTime.UtcNow.AddHours(2);
        var ctx = BaseContext(
            scheduledTime: scheduled,
            team1CheckedIn: true,
            team2CheckedIn: true);

        var result = service.CanCaptainGoLive(ctx, ctx.Team1Id, "ABCDE", DateTime.UtcNow);

        Assert.False(result.Allowed);
        Assert.Equal("go_live_too_early", result.Code);
    }

    [Fact]
    public void CanCheckIn_non_self_play_after_window_denied()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var scheduled = DateTime.UtcNow.AddHours(-1);
        var ctx = BaseContext(scheduledTime: scheduled) with
        {
            SchedulingConfig = new SchedulingConfigSnapshot(
                SelfPlayEnabled: false,
                CheckinWindowMinutes: 15,
                RoundDeadlines: new Dictionary<string, string>()),
            AcceptedProposalTime = null,
        };

        var result = service.CanCheckIn(ctx, ctx.Team1Id!.Value, DateTime.UtcNow);

        Assert.False(result.Allowed);
        Assert.Equal("checkin_window_closed", result.Code);
    }

    [Fact]
    public void IsCheckinWindow_closed_at_scheduled_match_time()
    {
        var scheduled = new DateTime(2026, 6, 15, 19, 0, 0, DateTimeKind.Utc);
        Assert.False(SelfPlayMatchRoomService.IsCheckinWindowOpen(scheduled, 15, scheduled));
        Assert.True(SelfPlayMatchRoomService.IsCheckinWindowClosed(scheduled, 15, scheduled));
    }

    [Fact]
    public void CanStaffForceGoLive_without_code_denied()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var ctx = BaseContext(scheduledTime: DateTime.UtcNow);
        var result = service.CanStaffForceGoLive(ctx, "  ", allowEmergencyWithoutCode: false, DateTime.UtcNow);
        Assert.False(result.Allowed);
        Assert.Equal("party_code_required", result.Code);
    }

    [Fact]
    public void CanStaffForceGoLive_with_code_allowed_without_checkins()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var ctx = BaseContext(scheduledTime: DateTime.UtcNow);
        var result = service.CanStaffForceGoLive(ctx, "ABCDE", allowEmergencyWithoutCode: false, DateTime.UtcNow);
        Assert.True(result.Allowed);
    }

    [Fact]
    public void CanStaffForceGoLive_emergency_force_allows_empty_code()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var ctx = BaseContext(scheduledTime: DateTime.UtcNow);
        var result = service.CanStaffForceGoLive(ctx, "  ", allowEmergencyWithoutCode: true, DateTime.UtcNow);
        Assert.True(result.Allowed);
    }

    [Fact]
    public void ValidateCaptainGoLiveTiming_too_early()
    {
        var scheduled = DateTime.UtcNow.AddHours(1);
        var result = SelfPlayMatchRoomService.ValidateCaptainGoLiveTiming(scheduled, DateTime.UtcNow);
        Assert.False(result.Allowed);
        Assert.Equal("go_live_too_early", result.Code);
    }

    [Fact]
    public void BuildRoomState_non_self_play_shows_organizer_message()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var ctx = BaseContext(scheduledTime: DateTime.UtcNow) with
        {
            SchedulingConfig = SchedulingConfigSnapshot.Default,
        };

        var room = service.BuildRoomState(ctx, null, false, DateTime.UtcNow);

        Assert.False(room.SelfPlayEnabled);
        Assert.Contains("party code", room.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveForfeitContext_double_no_show()
    {
        var ctx = BaseContext(status: "completed") with
        {
            Team1Score = 0,
            Team2Score = 0,
            WinnerId = null,
        };

        var (outcome, reason) = SelfPlayMatchRoomService.ResolveForfeitContext(ctx);

        Assert.Equal("double_forfeit", outcome);
        Assert.Equal("neither_checked_in", reason);
    }

    [Fact]
    public void BuildRoomState_double_forfeit_exposes_outcome_and_message()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var scheduled = DateTime.UtcNow.AddHours(-2);
        var ctx = BaseContext(status: "completed", scheduledTime: scheduled) with
        {
            Team1Score = 0,
            Team2Score = 0,
            WinnerId = null,
        };

        var room = service.BuildRoomState(ctx, ctx.Team1Id, false, DateTime.UtcNow);

        Assert.Equal("completed", room.Phase);
        Assert.Equal("double_forfeit", room.MatchOutcome);
        Assert.Equal("neither_checked_in", room.ForfeitReason);
        Assert.Contains("neither team checked in", room.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildRoomState_walkover_exposes_caller_specific_message()
    {
        var service = new SelfPlayMatchRoomService(null!);
        var scheduled = DateTime.UtcNow.AddHours(-2);
        var ctx = BaseContext(status: "completed", scheduledTime: scheduled, team1CheckedIn: true) with
        {
            WinnerId = null,
        };
        ctx = ctx with { WinnerId = ctx.Team1Id, Team1Score = 1, Team2Score = 0 };

        var winnerRoom = service.BuildRoomState(ctx, ctx.Team1Id, false, DateTime.UtcNow);
        var loserRoom = service.BuildRoomState(ctx, ctx.Team2Id, false, DateTime.UtcNow);

        Assert.Equal("walkover", winnerRoom.MatchOutcome);
        Assert.Equal("team2_not_checked_in", winnerRoom.ForfeitReason);
        Assert.Contains("Walkover win", winnerRoom.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Forfeited", loserRoom.Message, StringComparison.OrdinalIgnoreCase);
    }
}
