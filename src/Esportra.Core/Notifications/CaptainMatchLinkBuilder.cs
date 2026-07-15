using System.Data;
using Dapper;

namespace Esportra.Core.Notifications;

/// <summary>
/// Builds frontend captain match room URLs: /tournaments/{slug}/captain-match/{matchId}
/// </summary>
public static class CaptainMatchLinkBuilder
{
    public sealed record CaptainMatchContext(string? TournamentSlug);

    public static string BuildLink(string? tournamentSlug, Guid matchId) =>
        !string.IsNullOrWhiteSpace(tournamentSlug)
            ? $"/tournaments/{tournamentSlug}/captain-match/{matchId}"
            : "/tournaments";

    public static async Task<CaptainMatchContext> ResolveContextAsync(
        IDbConnection conn,
        Guid matchId,
        IDbTransaction? tx = null)
    {
        var slug = await conn.QuerySingleOrDefaultAsync<string?>(
            """
            SELECT t.slug
            FROM public.brkt_matches m
            JOIN public.brkt_versions v ON v.id = m.version_id
            JOIN public.tournaments t ON t.id = v.tournament_id
            WHERE m.id = @matchId
            """,
            new { matchId },
            tx);

        return new CaptainMatchContext(slug);
    }

    public static async Task<string> BuildAsync(
        IDbConnection conn,
        Guid matchId,
        IDbTransaction? tx = null)
    {
        var context = await ResolveContextAsync(conn, matchId, tx);
        return BuildLink(context.TournamentSlug, matchId);
    }
}
