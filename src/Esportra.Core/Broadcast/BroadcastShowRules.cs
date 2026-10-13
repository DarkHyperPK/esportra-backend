namespace Esportra.Core.Broadcast;

public static class BroadcastShowRules
{
    public static bool CanEditPlan(string status) => status is "draft" or "scheduled";

    public static bool CanSendConfiguration(string status) => status is "draft" or "scheduled" or "live";

    public static bool CanDelete(string status, bool hasHistory) => !hasHistory && status is "draft" or "scheduled" or "cancelled";

    public static string DuplicateTitle(string title) => $"{title[..Math.Min(title.Length, 193)]} (copy)";

    public static string? ValidateTitleChange(string status, string currentTitle, string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200)
            return "title_must_be_1_to_200_characters";
        if (status == "live" && !string.Equals(currentTitle, title.Trim(), StringComparison.Ordinal))
            return "show_live_title_locked";
        return null;
    }
}
