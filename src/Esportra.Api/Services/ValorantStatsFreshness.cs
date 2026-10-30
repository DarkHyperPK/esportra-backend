namespace Esportra.Api.Services;

internal static class ValorantStatsFreshness
{
    internal static readonly TimeSpan Window = TimeSpan.FromHours(1);

    internal static int Remaining(DateTime? timestamp) => timestamp is null ? 0 :
        Math.Max(0, (int)Math.Ceiling((timestamp.Value.Add(Window) - DateTime.UtcNow).TotalSeconds));

    internal static int Remaining(DateTime? syncedAt, DateTime? requestedAt) =>
        Math.Max(Remaining(syncedAt), Remaining(requestedAt));
}
