using System.Data;

namespace Esportra.Core.Tournaments;

/// <summary>
/// Immutable context passed to each format-specific standings resolver.
/// Carries the DB connection so resolvers share the connection opened by
/// StandingsResolutionService without opening additional connections.
/// </summary>
public sealed record StandingsContext(
    Guid TournamentId,
    Guid StageId,
    object? StageConfig,
    IDbConnection Conn
);

/// <summary>Contract every format resolver must implement.</summary>
public interface IStandingsResolver
{
    string Format { get; }
    Task<List<StandingsRow>> ResolveAsync(StandingsContext ctx, CancellationToken ct);
}
