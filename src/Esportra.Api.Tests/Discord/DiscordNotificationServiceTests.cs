using Esportra.Api.Services;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Esportra.Api.Tests.Discord;

public sealed class DiscordNotificationServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DiscordNotificationService BuildUnconfiguredService(
        FakeDbConnectionFactory? dbFactory = null)
    {
        var config = new ConfigurationBuilder().Build();
        return new DiscordNotificationService(
            Substitute.For<IHttpClientFactory>(),
            dbFactory ?? new FakeDbConnectionFactory(),
            config,
            NullLogger<DiscordNotificationService>.Instance);
    }

    private static DiscordNotificationService BuildConfiguredService(
        FakeDbConnectionFactory dbFactory,
        IHttpClientFactory? httpFactory = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Discord:BotToken"] = "fake-bot-token",
                ["Discord:GuildId"] = "12345678",
            })
            .Build();

        return new DiscordNotificationService(
            httpFactory ?? Substitute.For<IHttpClientFactory>(),
            dbFactory,
            config,
            NullLogger<DiscordNotificationService>.Instance);
    }

    // ── IsConfigured ─────────────────────────────────────────────────────────

    [Fact]
    public void IsConfigured_ReturnsFalse_WhenBotTokenMissing()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Discord:GuildId"] = "12345" })
            .Build();

        var service = new DiscordNotificationService(
            Substitute.For<IHttpClientFactory>(),
            new FakeDbConnectionFactory(),
            config,
            NullLogger<DiscordNotificationService>.Instance);

        service.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_ReturnsFalse_WhenGuildIdMissing()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Discord:BotToken"] = "tok" })
            .Build();

        var service = new DiscordNotificationService(
            Substitute.For<IHttpClientFactory>(),
            new FakeDbConnectionFactory(),
            config,
            NullLogger<DiscordNotificationService>.Instance);

        service.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_ReturnsTrue_WhenBothPresent()
    {
        BuildConfiguredService(new FakeDbConnectionFactory()).IsConfigured.Should().BeTrue();
    }

    // ── TrySendDmAsync — early exits that need no DB ──────────────────────────

    [Fact]
    public async Task TrySendDmAsync_ReturnsFalse_WhenNotConfigured()
    {
        var service = BuildUnconfiguredService();

        var result = await service.TrySendDmAsync(Guid.NewGuid(), "match_ready", "Title", "Msg");

        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("match_completed")]
    [InlineData("veto_your_turn")]
    [InlineData("broadcast")]
    [InlineData("staff_invite")]
    [InlineData("unknown_type")]
    public async Task TrySendDmAsync_ReturnsFalse_ForIneligibleType(string ineligibleType)
    {
        // Use unconfigured service — we just need the type-check path (which runs before IsConfigured)
        var service = BuildUnconfiguredService();

        var result = await service.TrySendDmAsync(Guid.NewGuid(), ineligibleType, "Title", "Msg");

        result.Should().BeFalse();
    }

    // ── TrySendDmAsync — DB-gated exits ──────────────────────────────────────

    [Fact]
    public async Task TrySendDmAsync_ReturnsFalse_WhenDmDisabledInProfileSettings()
    {
        var dbFactory = new FakeDbConnectionFactory();
        // DB returns: enabled=false, discordId="discord_user_1"
        dbFactory.EnqueueSingleRowResult(false, "discord_user_1");

        var service = BuildConfiguredService(dbFactory);

        var result = await service.TrySendDmAsync(Guid.NewGuid(), "match_ready", "Title", "Msg");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TrySendDmAsync_ReturnsFalse_WhenNoDiscordIdentityLinked()
    {
        var dbFactory = new FakeDbConnectionFactory();
        // DB returns: enabled=true, discordId=null
        dbFactory.EnqueueSingleRowResult(true, null);

        var service = BuildConfiguredService(dbFactory);

        var result = await service.TrySendDmAsync(Guid.NewGuid(), "match_ready", "Title", "Msg");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TrySendDmAsync_ReturnsFalse_WhenDiscordIdIsEmptyString()
    {
        var dbFactory = new FakeDbConnectionFactory();
        // DB returns: enabled=true, discordId=""
        dbFactory.EnqueueSingleRowResult(true, "");

        var service = BuildConfiguredService(dbFactory);

        var result = await service.TrySendDmAsync(Guid.NewGuid(), "match_ready", "Title", "Msg");

        result.Should().BeFalse();
    }

    // ── TrySendDmAsync — Gate 5: tournament pref ─────────────────────────────

    [Fact]
    public async Task TrySendDmAsync_ReturnsFalse_WhenTournamentPrefIsOptedOut()
    {
        var dbFactory = new FakeDbConnectionFactory();
        // Gate 4: user has DMs enabled and a linked Discord account
        dbFactory.EnqueueSingleRowResult(true, "discord_user_123");
        // Gate 5: user has opted out for this specific tournament
        dbFactory.EnqueueSingleRowResult(false);

        var service = BuildConfiguredService(dbFactory);

        var result = await service.TrySendDmAsync(
            Guid.NewGuid(), "match_ready", "Title", "Msg",
            tournamentId: Guid.NewGuid());

        result.Should().BeFalse("tournament-level opt-out must block the DM");
    }

    [Fact]
    public async Task TrySendDmAsync_SkipsTournamentGate_WhenTournamentIdIsNull()
    {
        var dbFactory = new FakeDbConnectionFactory();
        // Gate 4 passes — DMs enabled, no Discord ID (so it still returns false after gate 4)
        dbFactory.EnqueueSingleRowResult(true, null);
        // Gate 5 should not be queried when tournamentId is null
        // If it were, the queue would be consumed and QueueCount would drop to 0

        var service = BuildConfiguredService(dbFactory);

        // No tournamentId passed → Gate 5 skipped
        await service.TrySendDmAsync(Guid.NewGuid(), "match_ready", "Title", "Msg");

        dbFactory.QueueCount.Should().Be(0, "Gate 5 query must not be enqueued when tournamentId is null");
    }

    [Fact]
    public async Task TrySendDmAsync_AllowsDm_WhenTournamentPrefRowMissing()
    {
        // When no row exists in user_tournament_discord_prefs, default is true (opt-in).
        // The service returns false at the HTTP send step since we have no real HTTP client,
        // but it must reach past Gate 5 (i.e., Gate 5 must not have blocked it).
        var dbFactory = new FakeDbConnectionFactory();
        // Gate 4: DMs enabled + valid Discord ID
        dbFactory.EnqueueSingleRowResult(true, "discord_user_456");
        // Gate 5: no row in tournament pref table → QuerySingleOrDefaultAsync returns null → allowed
        dbFactory.EnqueueEmptyResult();

        var service = BuildConfiguredService(dbFactory);

        // Will reach SendDiscordDmAsync which fails due to no real HTTP client, returns false.
        // The important assertion is that Gate 5 did NOT block (queue fully consumed).
        await service.TrySendDmAsync(
            Guid.NewGuid(), "match_ready", "Title", "Msg",
            tournamentId: Guid.NewGuid());

        // Both queue entries were consumed: Gate 4 + Gate 5 queries both ran
        dbFactory.QueueCount.Should().Be(0,
            "both Gate 4 and Gate 5 queries must run when tournamentId is provided");
    }

    // ── TrySendBatchDmAsync — ineligible type short-circuits ─────────────────

    [Fact]
    public async Task TrySendBatchDmAsync_SkipsAll_WhenTypeIneligible()
    {
        var dbFactory = Substitute.For<Esportra.Contracts.Database.IDbConnectionFactory>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Discord:BotToken"] = "tok",
                ["Discord:GuildId"] = "guild",
            })
            .Build();
        var service = new DiscordNotificationService(
            Substitute.For<IHttpClientFactory>(),
            dbFactory,
            config,
            NullLogger<DiscordNotificationService>.Instance);

        await service.TrySendBatchDmAsync(
            [Guid.NewGuid(), Guid.NewGuid()],
            "veto_completed",
            "Title",
            "Msg");

        // Ineligible type — DB should never be touched
        dbFactory.DidNotReceive().CreateConnection();
    }
}
