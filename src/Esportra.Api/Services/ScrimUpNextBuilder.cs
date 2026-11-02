using Esportra.Contracts.Competitive;

namespace Esportra.Api.Services;

public sealed record UpNextOfferRow
{
    public Guid Id { get; init; }
    public Guid ListingId { get; init; }
    public Guid ListingTeamId { get; init; }
    public Guid OfferingTeamId { get; init; }
    public string? OfferingTeamName { get; init; }
    public string? OfferingTeamLogo { get; init; }
    public string? GameSlug { get; init; }
    public DateTime ProposedAt { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record UpNextSessionRow
{
    public Guid Id { get; init; }
    public Guid Team1Id { get; init; }
    public Guid Team2Id { get; init; }
    public string? Team1Name { get; init; }
    public string? Team1Logo { get; init; }
    public string? Team2Name { get; init; }
    public string? Team2Logo { get; init; }
    public string? GameSlug { get; init; }
    public string Status { get; init; } = null!;
    public DateTime? ScheduledAt { get; init; }
    public DateTime? Team1CheckedInAt { get; init; }
    public DateTime? Team2CheckedInAt { get; init; }
    /// <summary>Set by the check-in window job; the server rejects check-in until it is set.</summary>
    public DateTime? CheckinWindowOpenedAt { get; init; }
    public bool ResultReportedByTeam1 { get; init; }
    public bool ResultReportedByTeam2 { get; init; }
    public bool VetoOpen { get; init; }
    /// <summary>Team that made the pending time proposal, if one is awaiting a response.</summary>
    public Guid? PendingProposalByTeamId { get; init; }
    public string? DisputeStatus { get; init; }
    public Guid? DisputeRaisedByTeamId { get; init; }
    public int DisputeResponderEvidenceCount { get; init; }
}

/// <summary>
/// Turns raw offer/session rows into the prioritised list of things a team must do.
/// Pure and time-injected so the check-in window and expiry rules are unit-testable.
/// Windows mirror the schedule in AcceptOfferAsync: check-in opens at T-15 min and the
/// timeout job cancels at T+15 min.
/// </summary>
public static class ScrimUpNextBuilder
{
    public const int CheckinOpensBeforeMinutes = 15;
    public const int CheckinClosesAfterMinutes = 15;
    public const int OfferLifetimeHours = 48;
    /// <summary>Stop nagging about unreported results after this long.</summary>
    public const int ReportReminderHours = 72;
    public const int MaxItems = 50;

    private static readonly Dictionary<string, int> Priority = new()
    {
        ["checkin_open"] = 1,
        ["veto_ready"] = 2,
        ["result_to_confirm"] = 3,
        ["time_to_respond"] = 4,
        ["evidence_due"] = 5,
        ["result_to_report"] = 6,
        ["time_to_propose"] = 7,
        ["offer_received"] = 8,
    };

    public static ScrimUpNextResponse Build(
        IEnumerable<UpNextOfferRow> offers,
        IEnumerable<UpNextSessionRow> sessions,
        IReadOnlySet<Guid> myTeamIds,
        DateTime nowUtc)
    {
        var items = new List<ScrimUpNextItem>();

        foreach (var o in offers.Where(o => myTeamIds.Contains(o.ListingTeamId)))
        {
            var deadline = o.CreatedAt.AddHours(OfferLifetimeHours);
            if (deadline <= nowUtc) continue; // the expiry job just hasn't run yet
            items.Add(new ScrimUpNextItem
            {
                Type = "offer_received",
                TeamId = o.ListingTeamId,
                OfferId = o.Id,
                ListingId = o.ListingId,
                OpponentName = o.OfferingTeamName,
                OpponentLogo = o.OfferingTeamLogo,
                GameSlug = o.GameSlug,
                ScheduledAt = o.ProposedAt,
                Deadline = deadline,
            });
        }

        foreach (var s in sessions)
        {
            foreach (var isTeam1 in new[] { true, false })
            {
                var myId = isTeam1 ? s.Team1Id : s.Team2Id;
                if (!myTeamIds.Contains(myId)) continue;
                var item = ForSide(s, isTeam1, nowUtc);
                if (item is not null) items.Add(item);
            }
        }

        var ordered = items
            .OrderBy(i => Priority[i.Type])
            .ThenBy(i => i.Deadline ?? DateTime.MaxValue)
            .ToList();

        return new ScrimUpNextResponse { Items = ordered.Take(MaxItems).ToList(), Total = ordered.Count };
    }

    /// <summary>The time still has to be agreed: answer their proposal, or make one. Waiting on them is not an item.</summary>
    private static ScrimUpNextItem? SchedulingItem(UpNextSessionRow s, Guid myId, Func<string, DateTime?, ScrimUpNextItem> make)
    {
        if (s.PendingProposalByTeamId is { } by) return by != myId ? make("time_to_respond", null) : null;
        return s.ScheduledAt is null ? make("time_to_propose", null) : null;
    }

    private static ScrimUpNextItem? ForSide(UpNextSessionRow s, bool isTeam1, DateTime now)
    {
        var myId = isTeam1 ? s.Team1Id : s.Team2Id;
        var oppName = isTeam1 ? s.Team2Name : s.Team1Name;
        var oppLogo = isTeam1 ? s.Team2Logo : s.Team1Logo;
        var myCheckin = isTeam1 ? s.Team1CheckedInAt : s.Team2CheckedInAt;
        var iReported = isTeam1 ? s.ResultReportedByTeam1 : s.ResultReportedByTeam2;
        var theyReported = isTeam1 ? s.ResultReportedByTeam2 : s.ResultReportedByTeam1;

        ScrimUpNextItem Make(string type, DateTime? deadline = null) => new()
        {
            Type = type,
            TeamId = myId,
            SessionId = s.Id,
            OpponentName = oppName,
            OpponentLogo = oppLogo,
            GameSlug = s.GameSlug,
            ScheduledAt = s.ScheduledAt,
            Deadline = deadline,
        };

        switch (s.Status)
        {
            case "scheduled":
                // Check-in wins when it is open; the server only accepts it once the window job opened it,
                // so the queue must not send anyone to a check-in that would be rejected.
                if (s.ScheduledAt is { } start)
                {
                    var opens = start.AddMinutes(-CheckinOpensBeforeMinutes);
                    var closes = start.AddMinutes(CheckinClosesAfterMinutes);
                    if (s.CheckinWindowOpenedAt is not null && now >= opens && now <= closes && myCheckin is null)
                        return Make("checkin_open", closes);
                }
                return SchedulingItem(s, myId, Make);

            case "in_progress":
                if (theyReported && !iReported) return Make("result_to_confirm");
                if (s.VetoOpen && !iReported && !theyReported) return Make("veto_ready");
                if (!iReported && !theyReported
                    && (s.ScheduledAt is null || now - s.ScheduledAt.Value < TimeSpan.FromHours(ReportReminderHours)))
                    return Make("result_to_report");
                return null;

            case "disputed" when s.DisputeStatus == "open"
                                 && s.DisputeRaisedByTeamId is { } raisedBy && raisedBy != myId
                                 && s.DisputeResponderEvidenceCount == 0:
                return Make("evidence_due");

            default:
                return null;
        }
    }
}
