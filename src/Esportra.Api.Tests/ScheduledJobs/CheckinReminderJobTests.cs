using Esportra.Api.ScheduledJobs;
using Esportra.Api.Services;
using Esportra.Api.Tests.Infrastructure;
using Esportra.Contracts.Database;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Esportra.Api.Tests.ScheduledJobs;

public sealed class CheckinReminderJobTests
{
    private static readonly Guid MatchId = Guid.NewGuid();
    private static readonly Guid CaptainId1 = Guid.NewGuid();
    private static readonly Guid CaptainId2 = Guid.NewGuid();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Service with IsConfigured = false (no tokens in config).</summary>
    private static DiscordNotificationService BuildUnconfiguredService() =>
        new(
            Substitute.For<IHttpClientFactory>(),
            new FakeDbConnectionFactory(),
            new ConfigurationBuilder().Build(),
            NullLogger<DiscordNotificationService>.Instance);

    /// <summary>Service with IsConfigured = true (fake tokens).</summary>
    private static DiscordNotificationService BuildConfiguredService(FakeDbConnectionFactory serviceDb) =>
        new(
            Substitute.For<IHttpClientFactory>(),
            serviceDb,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Discord:BotToken"] = "fake-token",
                    ["Discord:GuildId"] = "123",
                })
                .Build(),
            NullLogger<DiscordNotificationService>.Instance);

    // ── Not configured ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NotConfigured_ReturnsWithoutQueryingDb()
    {
        var jobDb = Substitute.For<IDbConnectionFactory>();
        var discord = BuildUnconfiguredService();
        var config = new ConfigurationBuilder().Build();
        var job = new CheckinReminderJob(jobDb, discord, config, NullLogger<CheckinReminderJob>.Instance);

        await job.ExecuteAsync(MatchId, CancellationToken.None);

        jobDb.DidNotReceive().CreateConnection();
    }

    // ── Match not found ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MatchNotFound_ReturnsWithoutSendingDms()
    {
        var jobDb = new FakeDbConnectionFactory();
        jobDb.EnqueueEmptyResult(); // matchRow query returns no rows

        var serviceDb = Substitute.For<IDbConnectionFactory>();
        var discord = BuildConfiguredService(new FakeDbConnectionFactory());
        var logger = new CapturingLogger<CheckinReminderJob>();
        var config = new ConfigurationBuilder().Build();
        var job = new CheckinReminderJob(jobDb, discord, config, logger);

        await job.ExecuteAsync(MatchId, CancellationToken.None);

        // Job returned early — service DB (TrySendDmAsync) was never called
        serviceDb.DidNotReceive().CreateConnection();
        logger.Messages.Should().NotContain(m => m.Contains("Sent DM reminders"));
    }

    // ── Deadline already passed ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_DeadlinePassed_ReturnsWithoutSendingDms()
    {
        var jobDb = new FakeDbConnectionFactory();
        // matchRow: status=pending, deadline=1 minute ago
        jobDb.EnqueueSingleRowResult("pending", DateTime.UtcNow.AddMinutes(-1));

        var serviceDb = new FakeDbConnectionFactory(); // should not be touched
        var discord = BuildConfiguredService(serviceDb);
        var logger = new CapturingLogger<CheckinReminderJob>();
        var config = new ConfigurationBuilder().Build();
        var job = new CheckinReminderJob(jobDb, discord, config, logger);

        await job.ExecuteAsync(MatchId, CancellationToken.None);

        serviceDb.QueueCount.Should().Be(0, "TrySendDmAsync must not be called when the deadline has passed");
        logger.Messages.Should().NotContain(m => m.Contains("Sent DM reminders"));
    }

    // ── All participants already checked in ───────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AllParticipantsCheckedIn_DoesNotSendDms()
    {
        var jobDb = new FakeDbConnectionFactory();
        // matchRow: pending, future deadline
        jobDb.EnqueueSingleRowResult("pending", DateTime.UtcNow.AddHours(1));
        jobDb.EnqueueEmptyResult(); // gameSlug query (added in PROJ-023)
        // allCaptains query: one captain
        jobDb.EnqueueMultiRowResult([[CaptainId1]]);
        // checkedIn query: same captain is already checked in
        jobDb.EnqueueMultiRowResult([[CaptainId1]]);

        var serviceDb = new FakeDbConnectionFactory(); // should not be touched
        var discord = BuildConfiguredService(serviceDb);
        var logger = new CapturingLogger<CheckinReminderJob>();
        var config = new ConfigurationBuilder().Build();
        var job = new CheckinReminderJob(jobDb, discord, config, logger);

        await job.ExecuteAsync(MatchId, CancellationToken.None);

        serviceDb.QueueCount.Should().Be(0, "TrySendDmAsync must not be called when everyone is checked in");
        logger.Messages.Should().NotContain(m => m.Contains("Sent DM reminders"));
    }

    // ── Unchecked participants ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_UncheckedParticipants_SendsDmPerUncheckedCaptain()
    {
        var jobDb = new FakeDbConnectionFactory();
        // matchRow: pending, future deadline
        jobDb.EnqueueSingleRowResult("pending", DateTime.UtcNow.AddHours(1));
        jobDb.EnqueueEmptyResult(); // gameSlug query (added in PROJ-023)
        // allCaptains: two captains
        jobDb.EnqueueMultiRowResult([[CaptainId1], [CaptainId2]]);
        // checkedIn: only CaptainId1 checked in → CaptainId2 is unchecked
        jobDb.EnqueueMultiRowResult([[CaptainId1]]);

        // Service DB: returns default prefs when TrySendDmAsync queries user prefs.
        // default (bool, string?) = (false, null) → DMs disabled → returns false safely.
        var serviceDb = new FakeDbConnectionFactory();

        var discord = BuildConfiguredService(serviceDb);
        var logger = new CapturingLogger<CheckinReminderJob>();
        var config = new ConfigurationBuilder().Build();
        var job = new CheckinReminderJob(jobDb, discord, config, logger);

        await job.ExecuteAsync(MatchId, CancellationToken.None);

        // Job reached the DM loop and logged after iterating unchecked captains
        logger.Messages.Should().ContainSingle(
            m => m.Contains("Sent DM reminders") && m.Contains("1"),
            "one unchecked captain should trigger one DM attempt");
    }

    [Fact]
    public async Task ExecuteAsync_TwoCaptainsUnchecked_AttemptsDmForEach()
    {
        var jobDb = new FakeDbConnectionFactory();
        jobDb.EnqueueSingleRowResult("pending", DateTime.UtcNow.AddHours(1));
        jobDb.EnqueueEmptyResult(); // gameSlug query (added in PROJ-023)
        // both captains present, neither checked in
        jobDb.EnqueueMultiRowResult([[CaptainId1], [CaptainId2]]);
        jobDb.EnqueueEmptyResult(); // no checkins

        var serviceDb = new FakeDbConnectionFactory(); // returns default prefs each call
        var discord = BuildConfiguredService(serviceDb);
        var logger = new CapturingLogger<CheckinReminderJob>();
        var config = new ConfigurationBuilder().Build();
        var job = new CheckinReminderJob(jobDb, discord, config, logger);

        await job.ExecuteAsync(MatchId, CancellationToken.None);

        logger.Messages.Should().ContainSingle(
            m => m.Contains("Sent DM reminders") && m.Contains("2"),
            "two unchecked captains should produce one log line reporting count=2");
    }
}
