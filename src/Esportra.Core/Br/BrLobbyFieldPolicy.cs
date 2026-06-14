namespace Esportra.Core.Br;

/// <summary>
/// Lobby-level schedule/queue/map are rejected when the per-game schema is active.
/// </summary>
public static class BrLobbyFieldPolicy
{
    public const string PerGameFieldsError =
        "Schedule, queue timer, and map are set per game for this stage.";

    public static bool RejectsLobbyPerGameFieldsInBody(
        bool gamesModelReady,
        bool hasScheduledAt,
        bool hasQueueTimerMinutes,
        bool hasMap) =>
        gamesModelReady && (hasScheduledAt || hasQueueTimerMinutes || hasMap);
}
