using Esportra.Api.ScoreboardOcr;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Esportra.Api.Tests.ScoreboardOcr;

public sealed class ReportSourceResolverTests
{
    private static Task<(ReportSource? Source, string? Error)> Resolve(string? source, string? parseId, string? riotMatchId) =>
        ReportSourceResolver.ResolveAsync(
            Substitute.For<System.Data.IDbConnection>(), Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), source, parseId, riotMatchId);

    [Theory]
    [InlineData(null, null, "manual")]
    [InlineData(null, "riot-match-1", "riot")]
    [InlineData("MANUAL", null, "manual")]
    [InlineData("riot", "riot-match-1", "riot")]
    public async Task ResolveAsync_DerivesNonOcrSources_WithoutParse(string? source, string? riotMatchId, string expected)
    {
        var (resolved, error) = await Resolve(source, null, riotMatchId);

        error.Should().BeNull();
        resolved!.Source.Should().Be(expected);
        resolved.OcrParseId.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_Rejects_ParseIdOnNonOcrReports()
    {
        var (resolved, error) = await Resolve("manual", Guid.NewGuid().ToString(), null);

        resolved.Should().BeNull();
        error.Should().NotBeNull();
    }

    [Theory]
    [InlineData("ocr", null)]
    [InlineData("ocr", "not-a-guid")]
    [InlineData("vision", null)]
    public async Task ResolveAsync_Rejects_InvalidOcrOrUnknownSources(string source, string? parseId)
    {
        var (resolved, error) = await Resolve(source, parseId, null);

        resolved.Should().BeNull();
        error.Should().NotBeNull();
    }

    [Fact]
    public void WithOcrScreenshot_PrependsReadScreenshot_Once()
    {
        var url = "https://x/storage/v1/object/public/tournaments.results/matches/m/ocr/p.png";

        ReportSourceResolver.WithOcrScreenshot(["a"], url).Should().Equal(url, "a");
        ReportSourceResolver.WithOcrScreenshot([url, "a"], url).Should().Equal(url, "a");
        ReportSourceResolver.WithOcrScreenshot(null, null).Should().BeEmpty();
    }
}
