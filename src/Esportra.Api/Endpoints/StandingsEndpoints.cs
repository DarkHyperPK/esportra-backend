using System.Text.Json;
using Esportra.Api.Middleware;
using Esportra.Core.Tournaments;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

public static class StandingsEndpoints
{
    private static readonly JsonSerializerOptions s_snakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void MapStandingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tournaments/{id}/standings", async (
            Guid id,
            StandingsResolutionService service,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var result = await cache.GetOrCreateAsync(
                $"standings:{id}",
                async (_) => await service.ResolveAsync(id, ct),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30) },
                cancellationToken: ct);

            if (result is null) return Results.NotFound();
            return Results.Json(result, s_snakeCase);
        }).WithMetadata(new RateLimitPolicyMetadata("strict"))
          .WithTags("Standings");
    }
}
