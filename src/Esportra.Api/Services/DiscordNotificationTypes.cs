namespace Esportra.Api.Services;

public static class DiscordNotificationTypes
{
    public static readonly HashSet<string> DmEligibleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "match_ready",
        "match_completed",
        "match_walkover",
        "match_schedule_changed",
        "result_reported",
        "result_accepted",
        "result_disputed",
        "dispute_resolved",
        "tournament_registered",
        "tournament_announcement",
        "time_proposal_received",
        "time_proposal_accepted",
        "time_proposal_rejected",
        "time_proposal_countered",
        "time_proposal_expired",
        "checkin_open",
        "checkin_reminder",
        "party_code_submitted",
        "result_pending_response",
        "match_chat_message",
        "scheduling_escalation",
    };
}
