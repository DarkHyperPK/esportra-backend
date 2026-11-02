using Esportra.Contracts.Competitive;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

/// <summary>
/// Discover shows each team's next open times. Those must only ever be real, bookable slots.
/// </summary>
public sealed class ScrimNextSlotsContractTests
{
    private static string DiscoverQuery()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Esportra.Api")))
            dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Esportra.Api", "Endpoints", "ScrimEndpoints.cs"));
        var start = source.IndexOf("AS next_slots", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "discover must return next_slots");
        return source[Math.Max(0, start - 700)..(start + 20)];
    }

    [Fact]
    public void NextSlots_AreOnlyAvailableAndInTheFuture()
    {
        var q = DiscoverQuery();
        q.Should().Contain("sn.status = 'available'");
        q.Should().Contain("sn.starts_at > now()");
    }

    [Fact]
    public void NextSlots_AreSoonestFirst_AndCapped()
    {
        var q = DiscoverQuery();
        q.Should().Contain("ORDER BY sn.starts_at");
        q.Should().Contain("LIMIT 3");
    }

    [Fact]
    public void NextSlots_AreScopedToTheListingBeingReturned()
    {
        DiscoverQuery().Should().Contain("sn.listing_id = sl.id");
    }

    [Fact]
    public void ListingRow_DefaultsToNoSlots_SoOtherEndpointsAreUnaffected()
    {
        new ScrimListingRow().NextSlots.Should().BeNull();
    }
}
