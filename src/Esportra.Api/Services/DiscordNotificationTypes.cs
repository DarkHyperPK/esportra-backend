namespace Esportra.Api.Services;

public static class DiscordNotificationTypes
{
    public static readonly HashSet<string> DmEligibleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "match_ready",
        "result_reported",
        "result_disputed",
        "dispute_resolved",
        "dispute_rejected",
        "tournament_registered",
        "tournament_announcement",
        "tournament_invite",
        "result_accepted",
        "match_walkover",
        "match_schedule_changed",
        "match_chat_message",
        "checkin_open",
        "check_in_reminder",
        "party_code_submitted",
        "scheduling_escalation",
        "time_proposal_received",
        "time_proposal_accepted",
        "time_proposal_rejected",
        "time_proposal_countered",
        "team_invite",
        "team_invite_response",
        "team_captain_changed",
        "team_member_removed",
        "br_round_active",
        "dispute_reopened",
    };
}
