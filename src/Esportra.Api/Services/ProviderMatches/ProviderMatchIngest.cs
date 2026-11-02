using System.Data;
using System.Text.Json;
using Dapper;

namespace Esportra.Api.Services.ProviderMatches;

public sealed record IngestOutcome(string VerificationStatus, bool MatchCompleted, string? Failure);

/// <summary>
/// The part of ingestion that needs no Riot call: normalize a stored or freshly fetched payload, replace the derived
/// rows and apply verification, in one transaction. Re-running with the same payload leaves identical rows.
/// </summary>
public static class ProviderMatchIngest
{
    private sealed record AssociationRow(
        Guid MatchId, string ProviderMatchId, int? Team1Score, int? Team2Score, string? Map);

    private sealed record SnapshotRow(Guid? UserId, int Slot, string? Puuid);

    public static async Task<IngestOutcome> ApplyAsync(
        IDbConnection conn, Guid associationId, string payloadJson, string shard, bool storePayload)
    {
        var assoc = await conn.QuerySingleAsync<AssociationRow>(
            """
            SELECT a.match_id AS MatchId, m.provider_match_id AS ProviderMatchId, a.reported_team1_score AS Team1Score,
                   a.reported_team2_score AS Team2Score, a.reported_map AS Map
            FROM public.provider_match_associations a
            JOIN public.provider_matches m ON m.id = a.match_id
            WHERE a.id = @associationId
            """, new { associationId });

        NormalizedValorantMatch? match;
        using (var doc = JsonDocument.Parse(payloadJson))
            match = ValorantMatchNormalizer.Normalize(doc.RootElement);
        if (match is null || match.Info.ProviderMatchId != assoc.ProviderMatchId)
        {
            var reason = match is null ? "unparseable_payload" : "match_id_mismatch";
            await ProviderMatchStore.SetStatusAsync(conn, null, assoc.MatchId, "failed", reason, countAttempt: false);
            return new IngestOutcome("pending", false, reason);
        }

        using var tx = conn.BeginTransaction();
        await ProviderMatchStore.LockMatchAsync(conn, tx, assoc.ProviderMatchId);
        if (storePayload) await ProviderMatchStore.SavePayloadAsync(conn, tx, assoc.MatchId, payloadJson);
        await ProviderMatchStore.ReplaceNormalizedAsync(conn, tx, assoc.MatchId, shard, match);

        var roster = (await conn.QueryAsync<SnapshotRow>(
            """
            SELECT user_id AS UserId, team_slot::int AS Slot, puuid AS Puuid
            FROM public.provider_match_roster_snapshots
            WHERE association_id = @associationId AND team_slot IS NOT NULL
            """, new { associationId }, tx))
            .Select(r => new RosterMember(r.UserId, r.Slot, r.Puuid)).ToList();

        var result = ProviderMatchVerifier.Verify(new VerificationInput(match, assoc.Team1Score, assoc.Team2Score, assoc.Map, roster));
        await ProviderMatchStore.ApplyVerificationAsync(conn, tx, associationId, match, result);
        tx.Commit();
        return new IngestOutcome(result.Status, match.Info.IsCompleted, null);
    }

    /// <summary>No roster account is linked, so Riot cannot be asked and nothing about the players is invented.</summary>
    public static async Task MarkNoLinkedAccountsAsync(IDbConnection conn, Guid associationId, Guid matchId)
    {
        using var tx = conn.BeginTransaction();
        await ProviderMatchStore.SetStatusAsync(conn, tx, matchId, "unavailable", "no_linked_accounts", countAttempt: false);
        var changed = await conn.ExecuteAsync(
            """
            UPDATE public.provider_match_associations
            SET verification_status = 'unverified', verification_detail = '{"reasons":["no_linked_accounts"]}'::jsonb, updated_at = now()
            WHERE id = @associationId AND (verification_status <> 'unverified' OR verification_detail::text NOT LIKE '%no_linked_accounts%')
            """, new { associationId }, tx);
        if (changed > 0)
            await ProviderMatchRegistry.AddEventAsync(conn, tx, associationId, "verification", null, null,
                new { status = "unverified", reasons = new[] { "no_linked_accounts" } });
        tx.Commit();
    }
}
