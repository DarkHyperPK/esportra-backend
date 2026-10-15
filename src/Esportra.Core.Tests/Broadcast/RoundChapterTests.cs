using Esportra.Core.Broadcast;
using Xunit;

namespace Esportra.Core.Tests.Broadcast;

public sealed class RoundChapterTests
{
    [Fact]
    public void Round_chapters_coexist_with_segments_and_keep_original_capture_offsets()
    {
        var chapters = VodChapterCalculator.Compute([
            new("SegmentTake", "opening", "Opening", "pre_show", 11000),
            new("SegmentDone", "opening", "Opening", "pre_show", 21000),
            new("RoundStarted", "round:h:1", "HAVEN · Round 1", "round", 31000),
            new("RoundEnded", "round:h:1", "HAVEN · Round 1", "round", 71000),
            new("RoundStarted", "round:h:2", "HAVEN · Round 2", "round", 81000),
        ], 1000);
        Assert.Equal(3, chapters.Count);
        Assert.Equal(30, chapters[1].OffsetSeconds);
        Assert.Equal(40, chapters[1].DurationSeconds);
        Assert.False(chapters[1].Incomplete);
        Assert.True(chapters[2].Incomplete);
    }
}
