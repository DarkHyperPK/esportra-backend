using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;

namespace Esportra.Api.Services.ProviderMatches;

public static class ProviderMatchContext
{
    public const string ScrimGame = "scrim_game";
    public const string TournamentGame = "tournament_game";
}

public sealed record ProviderMatchRegistration(
    string ContextType, Guid ContextId, Guid? TournamentMatchId, int? GameNumber, string RiotMatchId,
    Guid Team1Id, Guid Team2Id, int? Team1Score, int? Team2Score, string? MapName,
    Guid? ActorUserId = null, string Method = "reported");

public sealed record RegistrationResult(Guid AssociationId, bool NeedsIngest);

/// <summary>
/// Records that a Riot match is the game an Esportra team reported, and freezes the rosters as they are at that
/// moment. Safe to call repeatedly: the same report changes nothing, a changed report is logged.
/// </summary>
public static class ProviderMatchRegistry
{
    /// <summary>Both teams' reports of one tournament game share this id.</summary>
    public static Guid TournamentGameContextId(Guid tournamentMatchId, int gameNumber) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"tournament-game:{tournamentMatchId:N}:{gameNumber}")));

    private sealed record Existing(Guid Id, Guid MatchId, int? Team1Score, int? Team2Score, string? Map);

    private sealed record RosterRow(Guid CompetitorId, Guid UserId, string? MemberRole, string? Puuid);

    public static async Task<RegistrationResult?> RegisterAsync(IDbConnection conn, ProviderMatchRegistration req)
    {
        if (!RiotMatchAccess.IsWellFormedMatchId(req.RiotMatchId)) return null;

        using var tx = conn.BeginTransaction();
        var matchId = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.provider_matches (provider_match_id) VALUES (@id)
            ON CONFLICT (provider, provider_match_id) DO UPDATE SET updated_at = provider_matches.updated_at
            RETURNING id
            """, new { id = req.RiotMatchId }, tx);

        var existing = await conn.QuerySingleOrDefaultAsync<Existing>(
            """
            SELECT id AS Id, match_id AS MatchId, reported_team1_score AS Team1Score,
                   reported_team2_score AS Team2Score, reported_map AS Map
            FROM public.provider_match_associations
            WHERE context_type = @ContextType AND context_id = @ContextId FOR UPDATE
            """, new { req.ContextType, req.ContextId }, tx);

        var claimedElsewhere = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM public.provider_match_associations
                           WHERE match_id = @matchId AND verification_status <> 'duplicate'
                             AND NOT (context_type = @ContextType AND context_id = @ContextId))
            """, new { matchId, req.ContextType, req.ContextId }, tx);
        var status = claimedElsewhere ? "duplicate" : "pending";

        Guid associationId;
        if (existing is null)
        {
            associationId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO public.provider_match_associations
                    (context_type, context_id, tournament_match_id, game_number, match_id, team1_id, team2_id,
                     reported_team1_score, reported_team2_score, reported_map, method, associated_by, verification_status)
                VALUES (@ContextType, @ContextId, @TournamentMatchId, @GameNumber, @matchId, @Team1Id, @Team2Id,
                        @Team1Score, @Team2Score, @MapName, @Method, @ActorUserId, @status)
                RETURNING id
                """, new { req.ContextType, req.ContextId, req.TournamentMatchId, req.GameNumber, matchId, req.Team1Id, req.Team2Id, req.Team1Score, req.Team2Score, req.MapName, req.Method, req.ActorUserId, status }, tx);
            await AddEventAsync(conn, tx, associationId, req.Method == "manual" ? "manual_association" : "registered", req.ActorUserId, null,
                new { match = req.RiotMatchId, req.Team1Score, req.Team2Score, req.MapName, status });
            await SnapshotRosterAsync(conn, tx, associationId, req.Team1Id, req.Team2Id);
        }
        else
        {
            associationId = existing.Id;
            if (existing.MatchId != matchId)
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE public.provider_match_associations
                    SET match_id = @matchId, reported_team1_score = @Team1Score, reported_team2_score = @Team2Score,
                        reported_map = @MapName, team1_riot_side = NULL, team2_riot_side = NULL,
                        verification_status = @status, verification_detail = '{}'::jsonb, verified_at = NULL,
                        method = @Method, associated_by = @ActorUserId, updated_at = now()
                    WHERE id = @associationId
                    """, new { matchId, req.Team1Score, req.Team2Score, req.MapName, status, req.Method, req.ActorUserId, associationId }, tx);
                // Riot-derived resolution of the old match goes; the roster identity captured at report time stays.
                await conn.ExecuteAsync("DELETE FROM public.provider_match_roster_snapshots WHERE association_id = @associationId AND identity_status = 'unmatched'", new { associationId }, tx);
                await conn.ExecuteAsync("UPDATE public.provider_match_roster_snapshots SET played = NULL, riot_side = NULL, riot_name = NULL WHERE association_id = @associationId", new { associationId }, tx);
                await AddEventAsync(conn, tx, associationId, req.Method == "manual" ? "manual_association" : "match_changed", req.ActorUserId,
                    new { match_ref = existing.MatchId }, new { match_ref = matchId, match = req.RiotMatchId, status });
            }
            else if (existing.Team1Score != req.Team1Score || existing.Team2Score != req.Team2Score || existing.Map != req.MapName)
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE public.provider_match_associations
                    SET reported_team1_score = @Team1Score, reported_team2_score = @Team2Score, reported_map = @MapName, updated_at = now()
                    WHERE id = @associationId
                    """, new { req.Team1Score, req.Team2Score, req.MapName, associationId }, tx);
                await AddEventAsync(conn, tx, associationId, "report_updated", req.ActorUserId,
                    new { existing.Team1Score, existing.Team2Score, existing.Map }, new { req.Team1Score, req.Team2Score, req.MapName });
            }
        }

        var stored = await conn.ExecuteScalarAsync<string>(
            "SELECT verification_status FROM public.provider_match_associations WHERE id = @associationId", new { associationId }, tx);
        tx.Commit();
        return new RegistrationResult(associationId, stored != "duplicate");
    }

    /// <summary>Active, non-coach members (or the solo participant) of both competitors, with their linked Riot account.</summary>
    private static async Task SnapshotRosterAsync(IDbConnection conn, IDbTransaction tx, Guid associationId, Guid team1Id, Guid team2Id)
    {
        var rows = await conn.QueryAsync<RosterRow>(
            """
            WITH members AS (
                SELECT tm.team_id AS competitor_id, tm.user_id, tm.role::text AS member_role
                FROM public.team_members tm
                WHERE tm.team_id = ANY(@ids) AND tm.is_active = TRUE AND tm.role <> 'coach'
                UNION ALL
                SELECT tp.id, tp.user_id, 'solo'
                FROM public.tournament_participants tp
                WHERE tp.id = ANY(@ids) AND tp.participant_type = 'solo' AND tp.user_id IS NOT NULL
            )
            SELECT m.competitor_id AS CompetitorId, m.user_id AS UserId, m.member_role AS MemberRole,
                   COALESCE(ra.puuid, pra.riot_puuid) AS Puuid
            FROM members m
            LEFT JOIN public.riot_accounts ra ON ra.user_id = m.user_id
            LEFT JOIN public.player_riot_accounts pra ON pra.user_id = m.user_id AND pra.verified_at IS NOT NULL
            LIMIT 60
            """, new { ids = new[] { team1Id, team2Id } }, tx);

        foreach (var r in rows.DistinctBy(r => r.UserId))
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.provider_match_roster_snapshots
                    (association_id, team_slot, team_id, user_id, puuid, member_role, identity_status)
                VALUES (@associationId, @slot, @CompetitorId, @UserId, @Puuid, @MemberRole, @identity)
                ON CONFLICT (association_id, user_id) WHERE user_id IS NOT NULL DO NOTHING
                """,
                new { associationId, slot = r.CompetitorId == team1Id ? 1 : 2, r.CompetitorId, r.UserId, r.Puuid, r.MemberRole, identity = r.Puuid is null ? "unlinked" : "linked" }, tx);
        }
    }

    public static Task AddEventAsync(
        IDbConnection conn, IDbTransaction? tx, Guid associationId, string type, Guid? actor, object? before, object? after) =>
        conn.ExecuteAsync(
            """
            INSERT INTO public.provider_match_association_events (association_id, event_type, actor_user_id, before_state, after_state)
            VALUES (@associationId, @type, @actor, @before::jsonb, @after::jsonb)
            """,
            new
            {
                associationId,
                type,
                actor,
                before = before is null ? null : JsonSerializer.Serialize(before),
                after = after is null ? null : JsonSerializer.Serialize(after),
            }, tx);
}
