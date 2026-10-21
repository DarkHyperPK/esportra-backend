using Esportra.Api.Services;
using Esportra.Api.Tests.Infrastructure;
using Esportra.Core.Licensing;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Esportra.Api.Tests.Licensing;

/// <summary>
/// Unit tests for <see cref="EventLicenseService"/> (BE-1).
/// Uses <see cref="FakeDbConnectionFactory"/> and a pass-through HybridCache stub
/// so no real database or cache is required.
/// </summary>
public sealed class EventLicenseServiceTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder().Build();

    // ── ReserveNew ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReserveNew_MaxTeamsOver32_ReturnsBracketUpgradeError()
    {
        var db = new FakeDbConnectionFactory();
        var cache = new PassthroughHybridCache();
        var svc = new EventLicenseService(db, cache, BuildConfig());

        var result = await svc.ReserveNewAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), maxTeams: 64);

        result.IsOk.Should().BeFalse();
        result.Error.Should().Be("bracket_upgrade_required");
        db.QueueCount.Should().Be(0, "no INSERT should be attempted when maxTeams > 32");
    }

    // ── IsOrganizerPro ────────────────────────────────────────────────────────
    // These tests use a stub subclass that overrides GetForTournamentAsync to
    // return a pre-set value, avoiding the need to drive Dapper with fake column names.

    [Fact]
    public async Task IsOrganizerPro_NullLicense_ReturnsTrue_Grandfathered()
    {
        var svc = new StubEventLicenseService(license: null);

        var result = await svc.IsOrganizerProAsync(Guid.NewGuid());

        result.Should().BeTrue("null license = grandfathered = Organizer Pro implicitly enabled");
    }

    [Fact]
    public async Task IsOrganizerPro_LicenseWithProFalse_ReturnsFalse()
    {
        var license = BuildLicense(hasOrganizerPro: false);
        var svc = new StubEventLicenseService(license);

        var result = await svc.IsOrganizerProAsync(Guid.NewGuid());

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsOrganizerPro_LicenseWithProTrue_ReturnsTrue()
    {
        var license = BuildLicense(hasOrganizerPro: true);
        var svc = new StubEventLicenseService(license);

        var result = await svc.IsOrganizerProAsync(Guid.NewGuid());

        result.Should().BeTrue();
    }

    // ── ResolveBracketTier ────────────────────────────────────────────────────

    [Theory]
    [InlineData(8, "free")]
    [InlineData(32, "free")]
    [InlineData(33, "<=64")]
    [InlineData(64, "<=64")]
    [InlineData(128, "<=128")]
    [InlineData(256, "<=256")]
    [InlineData(512, "<=512")]
    [InlineData(513, "custom")]
    public void ResolveBracketTier_ReturnsCorrectTier(int maxTeams, string expectedTier)
    {
        EventLicenseService.ResolveBracketTier(maxTeams).Should().Be(expectedTier);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EventLicense BuildLicense(bool hasOrganizerPro) =>
        new(
            Id: Guid.NewGuid(),
            TournamentId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            OrganizationId: Guid.NewGuid(),
            BracketTier: "free",
            BracketMaxTeams: 32,
            HasOrganizerPro: hasOrganizerPro,
            IsFreeQuota: true,
            Status: "active",
            CreatedAt: DateTime.UtcNow);
}

/// <summary>
/// Stub that overrides <see cref="EventLicenseService.GetForTournamentAsync"/> to return
/// a pre-set value, decoupling <see cref="EventLicenseService.IsOrganizerProAsync"/> tests
/// from Dapper's column-name mapping.
/// </summary>
internal sealed class StubEventLicenseService(EventLicense? license)
    : EventLicenseService(
        new FakeDbConnectionFactory(),
        new PassthroughHybridCache(),
        new ConfigurationBuilder().Build())
{
    public override Task<EventLicense?> GetForTournamentAsync(
        Guid tournamentId, CancellationToken ct = default)
        => Task.FromResult(license);
}

/// <summary>
/// Minimal HybridCache stub that bypasses the cache layer entirely:
/// factory is called on every GetOrCreateAsync, and set/remove are no-ops.
/// Suitable for unit tests that need deterministic, cache-free behavior.
/// </summary>
internal sealed class PassthroughHybridCache : HybridCache
{
    public override async ValueTask<T> GetOrCreateAsync<TState, T>(
        string key,
        TState state,
        Func<TState, CancellationToken, ValueTask<T>> factory,
        HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
        => await factory(state, cancellationToken);

    public override ValueTask RemoveAsync(
        string key, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public override ValueTask RemoveByTagAsync(
        string tag, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public override ValueTask SetAsync<T>(
        string key,
        T value,
        HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
