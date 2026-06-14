using Esportra.Core.Br;
using Xunit;

namespace Esportra.Core.Tests.Br;

public sealed class BrTimestampTests
{
    [Fact]
    public void CoerceTimestamp_maps_unspecified_datetime_as_utc()
    {
        var raw = new DateTime(2026, 6, 16, 16, 59, 0, DateTimeKind.Unspecified);

        var result = BrGameRepository.CoerceTimestamp(raw);

        Assert.NotNull(result);
        Assert.Equal(DateTimeKind.Utc, result!.Value.UtcDateTime.Kind);
        Assert.Equal(2026, result.Value.Year);
        Assert.Equal(6, result.Value.Month);
        Assert.Equal(16, result.Value.Day);
        Assert.Equal(16, result.Value.Hour);
        Assert.Equal(59, result.Value.Minute);
    }

    [Fact]
    public void CoerceTimestamp_parses_iso_string()
    {
        var result = BrGameRepository.CoerceTimestamp("2026-06-16T16:59:00Z");

        Assert.NotNull(result);
        Assert.Equal(2026, result!.Value.Year);
        Assert.Equal(6, result.Value.Month);
        Assert.Equal(16, result.Value.Day);
    }
}
