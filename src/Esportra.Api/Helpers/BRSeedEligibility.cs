namespace Esportra.Api.Helpers;

/// <summary>API compatibility shim — logic lives in <see cref="Esportra.Core.Br.BrSeedEligibility"/>.</summary>
public static class BRSeedEligibility
{
    public static readonly string[] DefaultStatuses = Esportra.Core.Br.BrSeedEligibility.DefaultStatuses;
    public static readonly string[] CheckInRequiredStatuses = Esportra.Core.Br.BrSeedEligibility.CheckInRequiredStatuses;

    public static Task<bool> IsCheckInRequiredAsync(
        System.Data.IDbConnection conn,
        Guid tournamentId,
        System.Data.IDbTransaction? tx = null) =>
        Esportra.Core.Br.BrSeedEligibility.IsCheckInRequiredAsync(conn, tournamentId, tx);

    public static string[] ResolveStatuses(bool checkInRequired) =>
        Esportra.Core.Br.BrSeedEligibility.ResolveStatuses(checkInRequired);

    public static string ParticipantSeedMessage(bool checkInRequired) =>
        Esportra.Core.Br.BrSeedEligibility.ParticipantSeedMessage(checkInRequired);

    public static string TeamSeedMessage(bool checkInRequired) =>
        Esportra.Core.Br.BrSeedEligibility.TeamSeedMessage(checkInRequired);
}
