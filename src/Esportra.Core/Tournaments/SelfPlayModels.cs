namespace Esportra.Core.Tournaments;

public enum SelfPlayPhase
{
    NeedsSchedule,
    AwaitingCheckIn,
    AwaitingPartyCode,
    AwaitingVeto,
    ReadyForMatch,
    Completed,
}

public enum SelfPlayNextAction
{
    None,
    ProposeTime,
    CheckIn,
    SubmitPartyCode,
    CompleteVeto,
    ReportResult,
}

public sealed record SchedulingConfigSnapshot(
    bool SelfPlayEnabled,
    int CheckinWindowMinutes,
    IReadOnlyDictionary<string, string> RoundDeadlines)
{
    public static SchedulingConfigSnapshot Default { get; } = new(
        SelfPlayEnabled: false,
        CheckinWindowMinutes: 15,
        RoundDeadlines: new Dictionary<string, string>());
}

public sealed record SelfPlayMatchRoomContext
{
    public required Guid MatchId { get; init; }
    public Guid? VersionId { get; init; }
    public int RoundIndex { get; init; }
    public string Status { get; init; } = "pending";
    public DateTime? MatchScheduledTime { get; init; }
    public string? PartyCode { get; init; }
    public Guid? Team1Id { get; init; }
    public Guid? Team2Id { get; init; }
    public int BestOf { get; init; } = 1;
    public Guid? StageId { get; init; }
    public Guid TournamentId { get; init; }
    public SchedulingConfigSnapshot SchedulingConfig { get; init; } = SchedulingConfigSnapshot.Default;
    public DateTime? TournamentStartDate { get; init; }
    public string? Game { get; init; }
    public string? GameMode { get; init; }
    public string? TournamentSettingsJson { get; init; }
    public bool Team1CheckedIn { get; init; }
    public bool Team2CheckedIn { get; init; }
    public DateTime? AcceptedProposalTime { get; init; }
    public string? VetoStatus { get; init; }
    public bool MapVetoEnabled { get; init; }
    public Guid? WinnerId { get; init; }
    public int? Team1Score { get; init; }
    public int? Team2Score { get; init; }
    public int PendingProposalCount { get; init; }
    public bool IsWalkover { get; init; }
}

public sealed record SelfPlayRoomState
{
    public required bool SelfPlayEnabled { get; init; }
    public string? Phase { get; init; }
    public string? NextAction { get; init; }
    public string? Message { get; init; }
    public DateTime? EffectiveScheduledTime { get; init; }
    public string? ScheduleSource { get; init; }
    public int CheckinWindowMinutes { get; init; }
    public bool CheckinWindowOpen { get; init; }
    public bool CheckinWindowClosed { get; init; }
    public DateTime? CheckinWindowOpensAt { get; init; }
    public DateTime? CheckinWindowClosesAt { get; init; }
    public DateTimeOffset? RoundDeadline { get; init; }
    public bool BothCheckedIn { get; init; }
    public bool Team1CheckedIn { get; init; }
    public bool Team2CheckedIn { get; init; }
    public Guid? Team1Id { get; init; }
    public Guid? Team2Id { get; init; }
    public Guid? CallerCompetitorId { get; init; }
    public bool CallerIsTeam1Captain { get; init; }
    public bool CallerCanForceGoLive { get; init; }
    public bool IsMatchLive { get; init; }
    public string? PartyCode { get; init; }
    public bool MapVetoEnabled { get; init; }
    public bool MapVetoCompleted { get; init; }
    public string? MatchOutcome { get; init; }
    public string? ForfeitReason { get; init; }
    public bool OpponentHasPendingProposal { get; init; }
    public int PendingProposalCount { get; init; }
}

public sealed record SelfPlayGuardResult
{
    public bool Allowed { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public string? Phase { get; init; }
    public string? NextAction { get; init; }

    public static SelfPlayGuardResult Allow() => new() { Allowed = true };

    public static SelfPlayGuardResult Deny(
        string code,
        string message,
        string? phase = null,
        string? nextAction = null)
        => new()
        {
            Allowed = false,
            Code = code,
            Message = message,
            Phase = phase,
            NextAction = nextAction,
        };
}
