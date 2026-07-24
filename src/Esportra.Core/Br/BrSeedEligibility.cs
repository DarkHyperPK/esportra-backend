using System.Data;
using Esportra.Core.Tournaments;

namespace Esportra.Core.Br;

public static class BrSeedEligibility
{
    public static readonly string[] DefaultStatuses = SeedEligibility.DefaultStatuses;
    public static readonly string[] CheckInRequiredStatuses = SeedEligibility.CheckInRequiredStatuses;

    public static Task<bool> IsCheckInRequiredAsync(
        IDbConnection conn,
        Guid tournamentId,
        IDbTransaction? tx = null) =>
        SeedEligibility.IsCheckInRequiredAsync(conn, tournamentId, tx);

    public static string[] ResolveStatuses(bool checkInRequired) =>
        SeedEligibility.ResolveStatuses(checkInRequired);

    public static string ParticipantSeedMessage(bool checkInRequired) =>
        SeedEligibility.ParticipantSeedMessage(checkInRequired);

    public static string TeamSeedMessage(bool checkInRequired) =>
        SeedEligibility.TeamSeedMessage(checkInRequired);
}
