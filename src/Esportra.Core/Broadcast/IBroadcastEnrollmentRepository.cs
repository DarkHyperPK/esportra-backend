namespace Esportra.Core.Broadcast;

public sealed record BroadcastEnrollmentResult(int Added, bool AtCapacity);
public interface IBroadcastEnrollmentRepository
{
    Task<BroadcastEnrollmentResult> EnrollAsync(Guid showId, CancellationToken ct);
    Task ProcessBatchAsync(CancellationToken ct);
}
