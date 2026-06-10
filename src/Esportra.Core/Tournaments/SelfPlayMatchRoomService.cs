using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Core.Tournaments;

/// <summary>
/// Authoritative self-play match room phase calculation and action guards.
/// </summary>
public sealed class SelfPlayMatchRoomService(IDbConnectionFactory db)
{
    private static readonly HashSet<string> BattleRoyaleGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "fortnite",
        "pubg",
        "pubg: battlegrounds",
        "apex legends",
        "call of duty: warzone",
        "warzone",
    };

    public async Task<SelfPlayMatchRoomContext?> LoadContextAsync(
        Guid matchId,
        bool mapVetoEnabledOverride,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleOrDefaultAsync<MatchRoomRow>(
            """
            SELECT
              m.id              AS MatchId,
              m.version_id      AS VersionId,
              m.round_index     AS RoundIndex,
              m.status          AS Status,
              m.scheduled_time  AS MatchScheduledTime,
              m.party_code      AS PartyCode,
              m.team1_id        AS Team1Id,
              m.team2_id        AS Team2Id,
              m.best_of         AS BestOf,
              v.stage_id        AS StageId,
              v.tournament_id   AS TournamentId,
              ts.scheduling_config::text AS SchedulingConfigJson,
              t.start_date      AS TournamentStartDate,
              t.game            AS Game,
              t.game_mode       AS GameMode,
              t.settings::text  AS TournamentSettingsJson
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            JOIN tournament_stages ts ON ts.id = v.stage_id
            JOIN tournaments t ON t.id = v.tournament_id
            WHERE m.id = @matchId
            """,
            new { matchId });

        if (row is null) return null;

        var checkins = (await conn.QueryAsync<Guid>(
            """
            SELECT team_id
            FROM match_checkins
            WHERE match_id = @matchId
            """,
            new { matchId })).ToHashSet();

        var acceptedProposalTime = await conn.QuerySingleOrDefaultAsync<DateTime?>(
            """
            SELECT proposed_time
            FROM match_time_proposals
            WHERE match_id = @matchId AND status = 'accepted'
            ORDER BY responded_at DESC NULLS LAST, created_at DESC
            LIMIT 1
            """,
            new { matchId });

        var vetoStatus = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT status FROM match_map_vetos WHERE match_id = @matchId",
            new { matchId });

        var schedulingConfig = SchedulingConfigParser.Parse(row.SchedulingConfigJson);
        var settingsMapVetoEnabled = ReadTournamentMapVetoEnabled(row.TournamentSettingsJson);

        return new SelfPlayMatchRoomContext
        {
            MatchId = row.MatchId,
            VersionId = row.VersionId,
            RoundIndex = row.RoundIndex,
            Status = row.Status ?? "pending",
            MatchScheduledTime = row.MatchScheduledTime,
            PartyCode = row.PartyCode,
            Team1Id = row.Team1Id,
            Team2Id = row.Team2Id,
            BestOf = row.BestOf,
            StageId = row.StageId,
            TournamentId = row.TournamentId,
            SchedulingConfig = schedulingConfig,
            TournamentStartDate = row.TournamentStartDate,
            Game = row.Game,
            GameMode = row.GameMode,
            TournamentSettingsJson = row.TournamentSettingsJson,
            Team1CheckedIn = row.Team1Id.HasValue && checkins.Contains(row.Team1Id.Value),
            Team2CheckedIn = row.Team2Id.HasValue && checkins.Contains(row.Team2Id.Value),
            AcceptedProposalTime = acceptedProposalTime,
            VetoStatus = vetoStatus,
            MapVetoEnabled = mapVetoEnabledOverride && settingsMapVetoEnabled,
        };
    }

    public SelfPlayRoomState BuildRoomState(
        SelfPlayMatchRoomContext ctx,
        Guid? callerCompetitorId,
        bool callerCanForceGoLive,
        DateTime nowUtc)
    {
        var selfPlayEnabled = IsSelfPlayActive(ctx);
        var (effectiveTime, scheduleSource) = ResolveEffectiveSchedule(ctx);
        var windowMinutes = ctx.SchedulingConfig.CheckinWindowMinutes;
        var bothCheckedIn = ctx.Team1CheckedIn && ctx.Team2CheckedIn;
        var status = NormalizeStatus(ctx.Status);
        var mapVetoCompleted = IsVetoCompleted(ctx);
        var isMatchLive = status == "in_progress";

        string? phase = null;
        string? nextAction = null;
        string? message = null;

        if (selfPlayEnabled)
        {
            var computedPhase = CalculatePhase(ctx, effectiveTime, bothCheckedIn, mapVetoCompleted);
            phase = ToApiPhase(computedPhase);
            nextAction = ToApiNextAction(ResolveNextAction(computedPhase, ctx, effectiveTime, bothCheckedIn, mapVetoCompleted));
            message = GetFlowMessage(
                computedPhase,
                ctx.MapVetoEnabled,
                callerCompetitorId.HasValue && ctx.Team1Id.HasValue && callerCompetitorId == ctx.Team1Id);
        }
        else if (status == "completed")
        {
            message = "This match is complete.";
        }
        else if (!isMatchLive)
        {
            message = ctx.MapVetoEnabled
                ? "Go live with a party code to unlock Map Veto and match actions"
                : "Go live with a party code to unlock match actions";
        }

        var windowOpen = effectiveTime.HasValue && IsCheckinWindowOpen(effectiveTime.Value, windowMinutes, nowUtc);
        var windowClosed = effectiveTime.HasValue && IsCheckinWindowClosed(effectiveTime.Value, windowMinutes, nowUtc);

        return new SelfPlayRoomState
        {
            SelfPlayEnabled = selfPlayEnabled,
            Phase = phase,
            NextAction = nextAction,
            Message = message,
            EffectiveScheduledTime = effectiveTime,
            ScheduleSource = scheduleSource,
            CheckinWindowMinutes = windowMinutes,
            CheckinWindowOpen = windowOpen,
            CheckinWindowClosed = windowClosed,
            BothCheckedIn = bothCheckedIn,
            Team1CheckedIn = ctx.Team1CheckedIn,
            Team2CheckedIn = ctx.Team2CheckedIn,
            Team1Id = ctx.Team1Id,
            Team2Id = ctx.Team2Id,
            CallerCompetitorId = callerCompetitorId,
            CallerIsTeam1Captain = callerCompetitorId.HasValue
                && ctx.Team1Id.HasValue
                && callerCompetitorId == ctx.Team1Id,
            CallerCanForceGoLive = callerCanForceGoLive,
            IsMatchLive = isMatchLive,
            PartyCode = ctx.PartyCode,
            MapVetoEnabled = ctx.MapVetoEnabled,
            MapVetoCompleted = mapVetoCompleted,
        };
    }

    public SelfPlayGuardResult CanCheckIn(
        SelfPlayMatchRoomContext ctx,
        Guid competitorId,
        DateTime nowUtc)
    {
        if (!IsSelfPlayActive(ctx))
            return SelfPlayGuardResult.Allow();

        var room = BuildPreviewState(ctx, nowUtc);
        var status = NormalizeStatus(ctx.Status);

        if (status != "pending")
            return Deny("match_not_pending", "Match is not pending check-in.", room);

        if (!ctx.Team1Id.HasValue || !ctx.Team2Id.HasValue)
            return Deny("match_missing_competitors", "Both competitors must be assigned before check-in.", room);

        if (ctx.Team1Id != competitorId && ctx.Team2Id != competitorId)
            return Deny("match_missing_competitors", "Competitor is not part of this match.", room);

        var (effectiveTime, _) = ResolveEffectiveSchedule(ctx);
        if (!effectiveTime.HasValue)
            return Deny("match_schedule_required", "A match time must be agreed before check-in.", room, SelfPlayNextAction.ProposeTime);

        if (IsCheckinWindowClosed(effectiveTime.Value, ctx.SchedulingConfig.CheckinWindowMinutes, nowUtc))
            return Deny("checkin_window_closed", "The check-in window has closed.", room, SelfPlayNextAction.None);

        if (!IsCheckinWindowOpen(effectiveTime.Value, ctx.SchedulingConfig.CheckinWindowMinutes, nowUtc))
            return Deny("checkin_window_not_open", "Check-in is not open yet.", room, SelfPlayNextAction.CheckIn);

        return SelfPlayGuardResult.Allow();
    }

    public SelfPlayGuardResult CanCaptainGoLive(
        SelfPlayMatchRoomContext ctx,
        Guid? callerCompetitorId,
        string? partyCode,
        DateTime nowUtc)
    {
        if (!IsSelfPlayActive(ctx))
            return SelfPlayGuardResult.Allow();

        var room = BuildPreviewState(ctx, nowUtc);
        var status = NormalizeStatus(ctx.Status);

        if (status != "pending")
            return Deny("match_not_pending", "Match is not pending go-live.", room);

        var (effectiveTime, _) = ResolveEffectiveSchedule(ctx);
        if (!effectiveTime.HasValue)
            return Deny("match_schedule_required", "A match time must be set before go-live.", room, SelfPlayNextAction.ProposeTime);

        if (effectiveTime.Value > nowUtc.AddMinutes(15))
            return Deny("match_schedule_required", $"Match is scheduled for {effectiveTime.Value:u}. Cannot go live more than 15 minutes early.", room);

        if (!ctx.Team1CheckedIn || !ctx.Team2CheckedIn)
            return Deny("self_play_checkins_required", "Both teams must check in before the match can go live.", room, SelfPlayNextAction.CheckIn);

        if (!callerCompetitorId.HasValue || ctx.Team1Id != callerCompetitorId)
            return Deny("team1_captain_required", "Only Team 1 captain can submit the party code.", room, SelfPlayNextAction.SubmitPartyCode);

        if (string.IsNullOrWhiteSpace(partyCode))
            return Deny("party_code_required", "Party code is required to go live.", room, SelfPlayNextAction.SubmitPartyCode);

        return SelfPlayGuardResult.Allow();
    }

    public SelfPlayGuardResult CanStaffForceGoLive(SelfPlayMatchRoomContext ctx, DateTime nowUtc)
        => SelfPlayGuardResult.Allow();

    public SelfPlayGuardResult CanProposeTime(SelfPlayMatchRoomContext ctx, DateTime nowUtc)
    {
        if (!IsSelfPlayActive(ctx))
            return SelfPlayGuardResult.Allow();

        var room = BuildPreviewState(ctx, nowUtc);
        var status = NormalizeStatus(ctx.Status);

        if (status != "pending")
            return Deny("match_not_pending", "Cannot propose a time after the match has started.", room);

        if (!ctx.Team1Id.HasValue || !ctx.Team2Id.HasValue)
            return Deny("match_missing_competitors", "Both competitors must be assigned before scheduling.", room);

        if (ctx.Team1CheckedIn || ctx.Team2CheckedIn)
            return Deny("match_not_pending", "Cannot change the schedule after check-in has started.", room);

        return SelfPlayGuardResult.Allow();
    }

    public SelfPlayGuardResult CanAcceptProposal(SelfPlayMatchRoomContext ctx, DateTime nowUtc)
        => CanProposeTime(ctx, nowUtc);

    public SelfPlayGuardResult CanSubmitResult(
        SelfPlayMatchRoomContext ctx,
        Guid competitorId,
        DateTime nowUtc)
    {
        if (!IsSelfPlayActive(ctx))
            return SelfPlayGuardResult.Allow();

        var room = BuildPreviewState(ctx, nowUtc);
        var status = NormalizeStatus(ctx.Status);

        if (status != "in_progress")
            return Deny("match_not_live", "Match must be live before reporting results.", room);

        if (ctx.Team1Id != competitorId && ctx.Team2Id != competitorId)
            return Deny("match_missing_competitors", "Reporting competitor is not part of this match.", room);

        if (ctx.MapVetoEnabled && !IsVetoCompleted(ctx))
            return Deny("veto_required", "Map veto must be completed before reporting results.", room, SelfPlayNextAction.CompleteVeto);

        return SelfPlayGuardResult.Allow();
    }

    public static (DateTime? Time, string? Source) ResolveEffectiveSchedule(SelfPlayMatchRoomContext ctx)
    {
        if (ctx.MatchScheduledTime.HasValue)
            return (ctx.MatchScheduledTime, "match_schedule");

        if (ctx.AcceptedProposalTime.HasValue)
            return (ctx.AcceptedProposalTime, "accepted_proposal");

        if (IsSelfPlayActive(ctx) && ctx.RoundIndex == 0 && ctx.TournamentStartDate.HasValue)
            return (ctx.TournamentStartDate, "tournament_start");

        return (null, null);
    }

    public static SelfPlayPhase CalculatePhase(
        SelfPlayMatchRoomContext ctx,
        DateTime? effectiveScheduledTime,
        bool bothCheckedIn,
        bool mapVetoCompleted)
    {
        var status = NormalizeStatus(ctx.Status);

        if (status == "completed")
            return SelfPlayPhase.Completed;

        if (!effectiveScheduledTime.HasValue)
            return SelfPlayPhase.NeedsSchedule;

        if (status == "pending")
            return bothCheckedIn ? SelfPlayPhase.AwaitingPartyCode : SelfPlayPhase.AwaitingCheckIn;

        if (status == "in_progress")
            return ctx.MapVetoEnabled && !mapVetoCompleted
                ? SelfPlayPhase.AwaitingVeto
                : SelfPlayPhase.ReadyForMatch;

        return SelfPlayPhase.AwaitingCheckIn;
    }

    public static SelfPlayNextAction ResolveNextAction(
        SelfPlayPhase phase,
        SelfPlayMatchRoomContext ctx,
        DateTime? effectiveScheduledTime,
        bool bothCheckedIn,
        bool mapVetoCompleted)
    {
        return phase switch
        {
            SelfPlayPhase.NeedsSchedule => SelfPlayNextAction.ProposeTime,
            SelfPlayPhase.AwaitingCheckIn => SelfPlayNextAction.CheckIn,
            SelfPlayPhase.AwaitingPartyCode => SelfPlayNextAction.SubmitPartyCode,
            SelfPlayPhase.AwaitingVeto => SelfPlayNextAction.CompleteVeto,
            SelfPlayPhase.ReadyForMatch => SelfPlayNextAction.ReportResult,
            _ => SelfPlayNextAction.None,
        };
    }

    public static string GetFlowMessage(SelfPlayPhase phase, bool mapVetoEnabled, bool isTeam1Captain)
    {
        return phase switch
        {
            SelfPlayPhase.NeedsSchedule =>
                "Propose and agree on a match time with your opponent to continue.",
            SelfPlayPhase.AwaitingCheckIn =>
                "Both teams must check in during the check-in window before the lobby code step.",
            SelfPlayPhase.AwaitingPartyCode => isTeam1Captain
                ? "Both teams are checked in. Team 1 captain: create the lobby and submit the party code."
                : "Both teams are checked in. Waiting for Team 1 to submit the party code.",
            SelfPlayPhase.AwaitingVeto => mapVetoEnabled
                ? "Match is live. Complete map veto to unlock result reporting."
                : "Match is live. You can now report results.",
            SelfPlayPhase.ReadyForMatch =>
                "Match is live. Map veto is complete — report results when ready.",
            SelfPlayPhase.Completed =>
                "This match is complete.",
            _ => string.Empty,
        };
    }

    public static bool IsCheckinWindowOpen(DateTime effectiveScheduledTime, int windowMinutes, DateTime nowUtc)
    {
        var windowStart = effectiveScheduledTime.AddMinutes(-windowMinutes);
        var windowEnd = effectiveScheduledTime.AddMinutes(windowMinutes);
        return nowUtc >= windowStart && nowUtc < windowEnd;
    }

    public static bool IsCheckinWindowClosed(DateTime effectiveScheduledTime, int windowMinutes, DateTime nowUtc)
    {
        var windowEnd = effectiveScheduledTime.AddMinutes(windowMinutes);
        return nowUtc >= windowEnd;
    }

    public static string ToApiPhase(SelfPlayPhase phase) => phase switch
    {
        SelfPlayPhase.NeedsSchedule => "needs_schedule",
        SelfPlayPhase.AwaitingCheckIn => "awaiting_checkin",
        SelfPlayPhase.AwaitingPartyCode => "awaiting_party_code",
        SelfPlayPhase.AwaitingVeto => "awaiting_veto",
        SelfPlayPhase.ReadyForMatch => "ready_for_match",
        SelfPlayPhase.Completed => "completed",
        _ => "awaiting_checkin",
    };

    public static string ToApiNextAction(SelfPlayNextAction action) => action switch
    {
        SelfPlayNextAction.ProposeTime => "propose_time",
        SelfPlayNextAction.CheckIn => "check_in",
        SelfPlayNextAction.SubmitPartyCode => "submit_party_code",
        SelfPlayNextAction.CompleteVeto => "complete_veto",
        SelfPlayNextAction.ReportResult => "report_result",
        _ => "none",
    };

    public static bool IsSelfPlayActive(SelfPlayMatchRoomContext ctx)
        => ctx.SchedulingConfig.SelfPlayEnabled && !IsBattleRoyaleGame(ctx.Game);

    private static bool IsVetoCompleted(SelfPlayMatchRoomContext ctx)
        => string.Equals(ctx.VetoStatus, "completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsBattleRoyaleGame(string? game)
    {
        if (string.IsNullOrWhiteSpace(game)) return false;
        var normalized = game.Trim();
        return BattleRoyaleGames.Contains(normalized);
    }

    private static bool ReadTournamentMapVetoEnabled(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("mapVetoEnabled", out var camel)
                && camel.ValueKind is JsonValueKind.False)
                return false;
            if (root.TryGetProperty("map_veto_enabled", out var snake)
                && snake.ValueKind is JsonValueKind.False)
                return false;
        }
        catch (JsonException)
        {
            // Default to enabled when settings are unreadable.
        }

        return true;
    }

    private static string NormalizeStatus(string? status)
        => (status ?? "pending").Trim().ToLowerInvariant();

    private SelfPlayRoomState BuildPreviewState(SelfPlayMatchRoomContext ctx, DateTime nowUtc)
        => BuildRoomState(ctx, callerCompetitorId: null, callerCanForceGoLive: false, nowUtc);

    private static SelfPlayGuardResult Deny(
        string code,
        string message,
        SelfPlayRoomState room,
        SelfPlayNextAction? nextAction = null)
        => SelfPlayGuardResult.Deny(
            code,
            message,
            room.Phase,
            nextAction.HasValue ? ToApiNextAction(nextAction.Value) : room.NextAction);

    private sealed record MatchRoomRow
    {
        public Guid MatchId { get; init; }
        public Guid? VersionId { get; init; }
        public int RoundIndex { get; init; }
        public string? Status { get; init; }
        public DateTime? MatchScheduledTime { get; init; }
        public string? PartyCode { get; init; }
        public Guid? Team1Id { get; init; }
        public Guid? Team2Id { get; init; }
        public int BestOf { get; init; }
        public Guid? StageId { get; init; }
        public Guid TournamentId { get; init; }
        public string? SchedulingConfigJson { get; init; }
        public DateTime? TournamentStartDate { get; init; }
        public string? Game { get; init; }
        public string? GameMode { get; init; }
        public string? TournamentSettingsJson { get; init; }
    }
}
