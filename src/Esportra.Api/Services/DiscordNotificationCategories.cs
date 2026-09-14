using System.Text.Json;

namespace Esportra.Api.Services;

public static class DiscordNotificationCategories
{
    private static readonly (string Name, string[] Types)[] Categories =
    [
        ("Match Alerts", ["match_ready", "match_walkover", "match_schedule_changed"]),
        ("Check-in", ["checkin_open", "check_in_reminder"]),
        ("Results & Disputes", ["result_reported", "result_accepted", "result_disputed", "dispute_resolved", "dispute_rejected", "dispute_reopened"]),
        ("Scheduling", ["scheduling_escalation", "time_proposal_received", "time_proposal_accepted", "time_proposal_rejected", "time_proposal_countered"]),
        ("Team", ["team_invite", "team_invite_response", "team_captain_changed", "team_member_removed"]),
        ("Tournament", ["tournament_registered", "tournament_announcement", "tournament_invite"]),
        ("Match Chat", ["match_chat_message"]),
        ("Party Codes", ["party_code_submitted"]),
        ("Battle Royale", ["br_round_active"]),
    ];

    private static readonly Dictionary<string, string> TypeToCategory =
        Categories.SelectMany(c => c.Types.Select(t => (Type: t, Category: c.Name)))
            .ToDictionary(x => x.Type, x => x.Category, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<NotificationCategory> GroupByCategory(IEnumerable<string> dmTypes)
    {
        var typeSet = new HashSet<string>(dmTypes, StringComparer.OrdinalIgnoreCase);

        return Categories
            .Select(c => new NotificationCategory(
                c.Name,
                c.Types.Where(t => typeSet.Contains(t)).ToArray()))
            .Where(c => c.Types.Length > 0)
            .ToList();
    }

    public static IReadOnlyList<string> ParseDmTypes(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return [];

        using var doc = JsonDocument.Parse(configJson);
        if (!doc.RootElement.TryGetProperty("dmTypes", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return [];

        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();
    }
}

public sealed record NotificationCategory(string Name, string[] Types);
