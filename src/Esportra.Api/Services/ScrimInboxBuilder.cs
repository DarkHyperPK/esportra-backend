using Esportra.Contracts.Competitive;

namespace Esportra.Api.Services;

public sealed record InboxOfferRow
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

public sealed record InboxSessionRow
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
    public bool ResultReportedByTeam1 { get; init; }
    public bool ResultReportedByTeam2 { get; init; }
    public bool VetoOpen { get; init; }
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
public static class ScrimInboxBuilder
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
        ["evidence_due"] = 4,
        ["result_to_report"] = 5,
        ["offer_received"] = 6,
    };

    public static ScrimInboxResponse Build(
        IEnumerable<InboxOfferRow> offers,
        IEnumerable<InboxSessionRow> sessions,
        IReadOnlySet<Guid> myTeamIds,
        DateTime nowUtc)
    {
        var items = new List<ScrimInboxItem>();

        foreach (var o in offers.Where(o => myTeamIds.Contains(o.ListingTeamId)))
        {
            var deadline = o.CreatedAt.AddHours(OfferLifetimeHours);
            if (deadline <= nowUtc) continue; // the expiry job just hasn't run yet
            items.Add(new ScrimInboxItem
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

        return new ScrimInboxResponse { Items = ordered.Take(MaxItems).ToList(), Total = ordered.Count };
    }

    private static ScrimInboxItem? ForSide(InboxSessionRow s, bool isTeam1, DateTime now)
    {
        var myId = isTeam1 ? s.Team1Id : s.Team2Id;
        var oppName = isTeam1 ? s.Team2Name : s.Team1Name;
        var oppLogo = isTeam1 ? s.Team2Logo : s.Team1Logo;
        var myCheckin = isTeam1 ? s.Team1CheckedInAt : s.Team2CheckedInAt;
        var iReported = isTeam1 ? s.ResultReportedByTeam1 : s.ResultReportedByTeam2;
        var theyReported = isTeam1 ? s.ResultReportedByTeam2 : s.ResultReportedByTeam1;

        ScrimInboxItem Make(string type, DateTime? deadline = null) => new()
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
            case "scheduled" when s.ScheduledAt is { } start:
                var opens = start.AddMinutes(-CheckinOpensBeforeMinutes);
                var closes = start.AddMinutes(CheckinClosesAfterMinutes);
                return now >= opens && now <= closes && myCheckin is null
                    ? Make("checkin_open", closes)
                    : null;

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
