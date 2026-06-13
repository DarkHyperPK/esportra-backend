using System.Data;
using Dapper;

namespace Esportra.Api.Helpers;

public static class BRSeedEligibility
{
    public static readonly string[] DefaultStatuses = ["approved", "checked_in"];
    public static readonly string[] CheckInRequiredStatuses = ["checked_in"];

    public static async Task<bool> IsCheckInRequiredAsync(
        IDbConnection conn,
        Guid tournamentId,
        IDbTransaction? tx = null)
    {
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT COALESCE(check_in_required, false)
            FROM public.tournaments
            WHERE id = @tournamentId
            """,
            new { tournamentId },
            tx);
    }

    public static string[] ResolveStatuses(bool checkInRequired) =>
        checkInRequired ? CheckInRequiredStatuses : DefaultStatuses;

    public static string ParticipantSeedMessage(bool checkInRequired) =>
        checkInRequired
            ? "No checked-in participants found. Participants must check in before seeding."
            : "No eligible participants found. Participants must be approved or checked in.";

    public static string TeamSeedMessage(bool checkInRequired) =>
        checkInRequired
            ? "No checked-in teams found. Teams must check in before seeding."
            : "No eligible teams found. Teams must be approved or checked in.";
}
