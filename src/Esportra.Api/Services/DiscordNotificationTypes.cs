namespace Esportra.Api.Services;

public static class DiscordNotificationTypes
{
    public static readonly HashSet<string> DmEligibleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "match_ready",
        "result_reported",
        "result_disputed",
        "dispute_resolved",
        "tournament_registered",
        "tournament_announcement",
        "result_accepted",
        "match_completed",
        "match_walkover",
        "match_schedule_changed",
        "match_chat_message",
        "checkin_open",
        "party_code_submitted",
        "scheduling_escalation",
    };
}
