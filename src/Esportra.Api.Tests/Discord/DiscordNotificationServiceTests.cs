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
