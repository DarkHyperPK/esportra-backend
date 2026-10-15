namespace Esportra.Contracts.Broadcast;

public sealed record NodeRuntimeStatus(
    string Mode,
    bool HudOnline,
    bool ObsConnected,
    string FeedState,
    bool AutomationPaused,
    bool TimeoutPaused,
    bool HudClientsConnected,
    string AppVersion,
    IReadOnlyList<NodeOutputHealth>? Outputs = null,
    NodeRundownPlan? Plan = null);

public sealed record NodeRundownPlan(Guid ShowId, long Version, long? PendingVersion = null);

public sealed record NodeOutputHealth(Guid Id, string Name, int ConnectionCount, DateTimeOffset? LastFrameAt);
