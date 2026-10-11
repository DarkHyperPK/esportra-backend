using Dapper;
using Esportra.Api.ScheduledJobs;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

internal static partial class PublicToolEndpoints
{
    private static async Task<IResult> LoadTrackedPublicVetoOverlayAsync(
        string token, IDbConnectionFactory db, PublicVetoOverlayUsageScheduler usage, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            """
            SELECT id, game, best_of, team1_name, team2_name, team1_id, team2_id, status,
                   selected_map_pool, current_team_id, current_action, current_action_number,
                   team1_banned_maps, team2_banned_maps, team1_picked_maps, team2_picked_maps,
                   selected_map_id, host_token, team1_token, team2_token, overlay_token,
                   toss_enabled, custom_sequence, expires_at, started_at, completed_at,
                   toss_winner_team_id, toss_completed_at, toss_first_actor_team_id,
                   overlay_first_loaded_at
            FROM public.public_veto_sessions
            WHERE overlay_token = @token AND expires_at > now()
            """, new { token }, cancellationToken: ct));
        if (row is null) return Results.NotFound();
        var response = await ToPublicVetoResponseAsync(conn, row, token);
        ct.ThrowIfCancellationRequested();
        if (row.overlay_first_loaded_at is null)
            usage.Enqueue((Guid)row.id, DateTime.UtcNow);
        return Results.Ok(response);
    }
}
