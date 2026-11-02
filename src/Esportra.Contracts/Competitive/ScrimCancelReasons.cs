namespace Esportra.Contracts.Competitive;

/// <summary>
/// Allowed values of scrim_sessions.cancelled_reason. Must match the CHECK constraint
/// chk_scrim_sessions_cancelled_reason (guarded by ScrimCancelReasonContractTests).
/// </summary>
public static class ScrimCancelReasons
{
    public const string CheckinTimeout = "checkin_timeout";
    public const string CancelledByTeam = "cancelled_by_team";
    public const string ListingClosed = "listing_closed";
    public const string Admin = "admin";

    public static readonly IReadOnlyList<string> All =
        [CheckinTimeout, CancelledByTeam, ListingClosed, Admin];
}
