using System.Text.Json;
using Dapper;
using Esportra.Contracts.Database;
using Esportra.Core.Match;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Infrastructure.Database;

public sealed class VetoSettingsRepository(IDbConnectionFactory db, HybridCache cache) : IVetoSettingsRepository
{
    private sealed record SettingsRow(Guid MatchId, string Mode, string? Sequence);

    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(60),
    };

    public async Task<VetoSettings?> GetAsync(Guid matchId, CancellationToken ct = default)
    {
        return await cache.GetOrCreateAsync(
            $"match-veto-settings:{matchId}",
            async _ => await FetchFromDbAsync(matchId, ct),
            CacheOptions,
            cancellationToken: ct);
    }

    public async Task SaveAsync(VetoSettings settings, Guid updatedBy, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var sequenceJson = settings.Sequence is not null
            ? JsonSerializer.Serialize(settings.Sequence, Esportra.Core.JsonDefaults.SnakeCase)
            : null;
        var modeStr = settings.Mode == VetoMode.Custom ? "custom" : "default";

        await conn.ExecuteAsync(
            """
            INSERT INTO public.match_veto_settings (match_id, mode, sequence, updated_at, updated_by)
            VALUES (@matchId, @mode, @sequence::jsonb, NOW(), @updatedBy)
            ON CONFLICT (match_id) DO UPDATE SET
                mode = @mode,
                sequence = @sequence::jsonb,
                updated_at = NOW(),
                updated_by = @updatedBy
            """,
            new { matchId = settings.MatchId, mode = modeStr, sequence = sequenceJson, updatedBy });

        await cache.RemoveAsync($"match-veto-settings:{settings.MatchId}", ct);
    }

    public async Task ClearAsync(Guid matchId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM public.match_veto_settings WHERE match_id = @matchId",
            new { matchId });

        await cache.RemoveAsync($"match-veto-settings:{matchId}", ct);
    }

    private async Task<VetoSettings?> FetchFromDbAsync(Guid matchId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<SettingsRow>(
            """
            SELECT match_id, mode, sequence::text AS sequence
            FROM public.match_veto_settings
            WHERE match_id = @matchId
            """,
            new { matchId });

        if (row is null) return null;

        var mode = row.Mode == "custom" ? VetoMode.Custom : VetoMode.Default;
        VetoStep[]? sequence = null;
        if (row.Sequence is not null && !string.IsNullOrWhiteSpace(row.Sequence))
            sequence = JsonSerializer.Deserialize<VetoStep[]>(row.Sequence, Esportra.Core.JsonDefaults.SnakeCase);

        return new VetoSettings(row.MatchId, mode, sequence);
    }
}
