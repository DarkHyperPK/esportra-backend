namespace Esportra.Api.Services;

/// <summary>
/// Who to tell, and what to say, when a team cancels a scrim. The cancelling team is
/// never notified about their own action. Pure so the rules are unit-tested.
/// </summary>
public sealed record ScrimCancelNotice(Guid RecipientTeamId, string Type, string Title, string Message)
{
    public const string NotificationType = "scrim_session_cancelled";

    /// <summary>Null when the cancelling team is unknown or is not one of the two participants.</summary>
    public static ScrimCancelNotice? For(Guid team1Id, Guid team2Id, Guid? cancelledByTeamId, string? cancellerName)
    {
        if (cancelledByTeamId is not { } by) return null;
        Guid recipient;
        if (by == team1Id) recipient = team2Id;
        else if (by == team2Id) recipient = team1Id;
        else return null;

        var who = string.IsNullOrWhiteSpace(cancellerName) ? "The other team" : cancellerName.Trim();
        return new ScrimCancelNotice(
            recipient,
            NotificationType,
            "Scrim cancelled",
            $"{who} cancelled your scrim. You can find another opponent from the scrim listings.");
    }
}
