using Esportra.Contracts.Broadcast;

namespace Esportra.Core.Broadcast;

public static class NodeRuntimeRules
{
    public static bool IsValid(NodeRuntimeStatus status) =>
        status.Mode is "production" or "capture-only" &&
        status.FeedState is "waiting" or "live" or "stale" or "idle" &&
        status.AppVersion is { Length: > 0 and <= 64 } &&
        !status.AppVersion.Any(char.IsControl) &&
        (status.Plan is null || status.Mode == "production" && status.Plan.ShowId != Guid.Empty &&
            status.Plan.Version is > 0 and <= 9007199254740991 &&
            (status.Plan.PendingVersion is null || status.Plan.PendingVersion > status.Plan.Version && status.Plan.PendingVersion <= 9007199254740991)) &&
        (status.Outputs is null || status.Outputs.Count <= 100 && status.Outputs.All(output =>
            output is not null && output.Id != Guid.Empty && output.Name is { Length: > 0 and <= 80 } &&
            !output.Name.Any(char.IsControl) && output.ConnectionCount is >= 0 and <= 10000));
}
