using System.Data;
using Dapper;

namespace Esportra.Api.Services;

/// <summary>Who hears about a tournament's problems: its organizer, organization admins and assigned staff.</summary>
public static class TournamentOrganizerRecipients
{
    public static async Task<List<Guid>> LoadAsync(IDbConnection conn, Guid tournamentId) =>
        (await conn.QueryAsync<Guid>(
            """
            SELECT DISTINCT r.user_id
            FROM (
                SELECT t.organizer_id AS user_id
                FROM public.tournaments t
                WHERE t.id = @tournamentId
                  AND t.organizer_id IS NOT NULL

                UNION

                SELECT os.user_id
                FROM public.tournaments t
                JOIN public.organizations o ON o.id = t.organization_id
                JOIN public.organization_staff os
                  ON os.organization_id = o.id
                 AND os.status = 'active'
                LEFT JOIN public.staff_tournament_assignments sta
                  ON sta.organization_staff_id = os.id
                 AND sta.tournament_id = t.id
                WHERE t.id = @tournamentId
                  AND (os.role = 'admin' OR sta.id IS NOT NULL)
            ) r
            WHERE r.user_id IS NOT NULL
            """,
            new { tournamentId })).AsList();
}
