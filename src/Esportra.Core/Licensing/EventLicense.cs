namespace Esportra.Core.Licensing;

/// <summary>
/// Represents a single event license row from the event_licenses table.
/// One license per tournament. Null = legacy tournament (grandfathered).
/// </summary>
public sealed record EventLicense(
    Guid Id,
    Guid TournamentId,
    Guid UserId,
    Guid OrganizationId,
    string BracketTier,
    int BracketMaxTeams,
    bool HasOrganizerPro,
    bool IsFreeQuota,
    string Status,
    DateTime CreatedAt);

/// <summary>Discriminated result for service operations that can fail with a known error code.</summary>
public sealed record LicenseResult<T>(T? Value, string? Error)
{
    public static LicenseResult<T> Ok(T value) => new(value, null);
    public static LicenseResult<T> Err(string error) => new(default, error);
    public bool IsOk => Error is null;
}
