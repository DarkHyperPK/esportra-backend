namespace Esportra.Core.Broadcast;

/// <summary>
/// One raw row from broadcast_events for a SegmentTake or SegmentDone event.
/// Mapped from Dapper; wallClockMs comes from payload->'data'->>'wallClockMs'.
/// </summary>
public sealed record SegmentEvent(
    string EventType,
    string? SegmentId,
    string? SegmentName,
    string? SegmentType,
    long WallClockMs);

/// <summary>
/// A computed VOD chapter derived from a segment's lifecycle events.
/// </summary>
public sealed record VodChapter(
    string SegmentId,
    string SegmentName,
    string? SegmentType,
    double OffsetSeconds,
    double? DurationSeconds,
    bool Incomplete);

/// <summary>
/// Pure computation: derives ordered VOD chapters from a flat list of
/// SegmentTake / SegmentDone events and the show's start wall-clock time.
/// No I/O; safe to unit-test without infrastructure.
/// </summary>
public static class VodChapterCalculator
{
    /// <summary>
    /// Computes chapters from raw segment events.
    /// </summary>
    /// <param name="events">All SegmentTake and SegmentDone events for the show.</param>
    /// <param name="showStartedAtMs">Show start as Unix milliseconds (from started_at).</param>
    /// <returns>Chapters ordered by offsetSeconds ascending.</returns>
    public static IReadOnlyList<VodChapter> Compute(
        IEnumerable<SegmentEvent> events,
        long showStartedAtMs)
    {
        var grouped = events
            .Where(e => !string.IsNullOrEmpty(e.SegmentId))
            .GroupBy(e => e.SegmentId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var chapters = new List<VodChapter>(grouped.Count);

        foreach (var (segmentId, segEvents) in grouped)
        {
            var takes = segEvents
                .Where(e => e.EventType is "SegmentTake" or "RoundStarted")
                .OrderBy(e => e.WallClockMs)
                .ToList();

            if (takes.Count == 0) continue;

            // Re-armed segment: use the LAST SegmentTake (highest wallClockMs)
            var lastTake = takes[^1];

            // Find the first SegmentDone at or after the last SegmentTake
            var done = segEvents
                .Where(e => (e.EventType is "SegmentDone" or "RoundEnded") && e.WallClockMs >= lastTake.WallClockMs)
                .OrderBy(e => e.WallClockMs)
                .FirstOrDefault();

            var offsetSeconds = (lastTake.WallClockMs - showStartedAtMs) / 1000.0;

            double? durationSeconds = null;
            var incomplete = true;

            if (done is not null)
            {
                durationSeconds = (done.WallClockMs - lastTake.WallClockMs) / 1000.0;
                incomplete = false;
            }

            chapters.Add(new VodChapter(
                SegmentId: segmentId,
                SegmentName: lastTake.SegmentName ?? string.Empty,
                SegmentType: lastTake.SegmentType,
                OffsetSeconds: offsetSeconds,
                DurationSeconds: durationSeconds,
                Incomplete: incomplete));
        }

        return chapters.OrderBy(c => c.OffsetSeconds).ToList().AsReadOnly();
    }
}
