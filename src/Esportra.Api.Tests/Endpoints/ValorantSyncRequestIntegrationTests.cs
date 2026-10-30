using System.Net;
using System.Text.Json;
using Dapper;
using Esportra.Api.Endpoints;
using Esportra.Api.Services;
using Esportra.Api.Tests.Infrastructure;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

[Collection(IntegrationCollection.Name)]
public sealed class ValorantSyncRequestIntegrationTests(ApiFactory factory)
{
    private IDbConnectionFactory Db => factory.Services.GetRequiredService<IDbConnectionFactory>();
    private static DefaultHttpContext Context(Guid user)
    {
        var context = new DefaultHttpContext();
        context.Items["UserContext"] = new UserContext { UserId = user.ToString() };
        return context;
    }
    private static IBackgroundJobClient Jobs()
    {
        var jobs = Substitute.For<IBackgroundJobClient>();
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("test-job");
        return jobs;
    }
    private ValorantSyncRequestService Service(IBackgroundJobClient jobs) => new(Db, jobs, NullLogger<ValorantSyncRequestService>.Instance);
    private static JsonElement Body(IResult result) => JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value);

    private async Task<(Guid Owner, Guid Listing)> Listing(string status = "published", bool expired = false)
    {
        var owner = Guid.NewGuid();
        var listing = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(owner);
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@owner, @puuid, 'Player', 'TAG');
            INSERT INTO public.lfg_listings (id, user_id, game_slug, intent, status, expires_at)
            VALUES (@listing, @owner, 'valorant', 'looking_for_team', @status::public.lfg_listing_status, @expires)
            """, new { owner, listing, puuid = owner.ToString(), status, expires = DateTime.UtcNow.AddDays(expired ? -1 : 1) });
        return (owner, listing);
    }

    [Fact]
    public async Task VisibleListing_ConcurrentViewersAndOwner_ShareOneDurableRequest()
    {
        var (owner, listing) = await Listing();
        var viewers = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var viewer in viewers) await factory.Seeder.SeedAuthUserAsync(viewer);
        var jobs = Jobs();
        var service = Service(jobs);
        var responses = await Task.WhenAll(viewers.Select(viewer => LfgStatsSyncEndpoints.SyncListingAsync(listing, Context(viewer), Db, service, CancellationToken.None)));
        responses.Select(result => Body(result).GetProperty("status").GetString()).Should().Contain("queued");
        jobs.Received(1).Create(Arg.Is<Job>(job => job.Type == typeof(ValorantStatsSyncService) && job.Args.Count == 3 && Equals(job.Args[1], true)), Arg.Any<IState>());
        Body(await LfgStatsSyncEndpoints.SyncMineAsync(Context(owner), service, CancellationToken.None)).GetProperty("status").GetString().Should().Be("pending");
        await using var conn = factory.Seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<bool>("SELECT sync_status = 'pending' AND sync_requested_at IS NOT NULL AND synced_at IS NULL FROM public.player_game_stats WHERE user_id = @owner", new { owner })).Should().BeTrue();
        using var client = factory.CreateAuthenticatedClient(viewers[0]);
        var state = await client.GetAsync($"/api/competitive/lfg/{listing}/stats-sync");
        state.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await state.Content.ReadAsStringAsync();
        json.Should().NotContain("puuid").And.NotContain(owner.ToString()).And.NotContain("headshots");
    }

    [Fact]
    public async Task AutomaticAndViewerRefresh_ShareTheSameClaim()
    {
        var (owner, listing) = await Listing();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.player_game_stats
                (user_id, game_slug, provider_player_id, last_act_id, synced_at, updated_at, sync_status)
            VALUES (@owner, 'valorant', @puuid, 'current', now() - interval '2 hours', now() - interval '20 minutes', 'partial')
            """, new { owner, puuid = owner.ToString() });
        var jobs = Jobs();
        var service = Service(jobs);
        await Task.WhenAll(
            LfgEndpoints.GetMyGameStatsAsync("valorant", Context(owner), Db, service, CancellationToken.None),
            LfgStatsSyncEndpoints.SyncListingAsync(listing, Context(Guid.NewGuid()), Db, service, CancellationToken.None));
        jobs.Received(1).Create(Arg.Is<Job>(job => job.Args.Count == 3 && Equals(job.Args[1], true)), Arg.Any<IState>());
    }

    [Fact]
    public async Task FinishedSync_CooldownAppliesAcrossViewers_AndAllowsLaterRepair()
    {
        var (owner, listing) = await Listing();
        var jobs = Jobs();
        var service = Service(jobs);
        await LfgStatsSyncEndpoints.SyncListingAsync(listing, Context(owner), Db, service, CancellationToken.None);
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE public.player_game_stats SET sync_status = 'partial', last_act_id = 'current', synced_at = now() WHERE user_id = @owner", new { owner });
        var response = Body(await LfgStatsSyncEndpoints.SyncListingAsync(listing, Context(Guid.NewGuid()), Db, service, CancellationToken.None));
        response.GetProperty("status").GetString().Should().Be("cached");
        response.GetProperty("retry_after_seconds").GetInt32().Should().BeInRange(1, 3600);
        jobs.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
        await conn.ExecuteAsync("UPDATE public.player_game_stats SET sync_requested_at = now() - interval '61 minutes', synced_at = now() - interval '61 minutes' WHERE user_id = @owner", new { owner });
        Body(await LfgStatsSyncEndpoints.SyncMineAsync(Context(owner), service, CancellationToken.None)).GetProperty("status").GetString().Should().Be("queued");
        jobs.Received(2).Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    [Fact]
    public async Task QueueFailure_PreservesStats_AndReleasesRequestForRetry()
    {
        var (owner, _) = await Listing();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, win_rate, hs_pct, sync_status) VALUES (@owner, 'valorant', @puuid, 53, 25, 'complete')", new { owner, puuid = owner.ToString() });
        var jobs = Jobs();
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(_ => throw new InvalidOperationException("queue unavailable"));
        var result = await LfgStatsSyncEndpoints.SyncMineAsync(Context(owner), Service(jobs), CancellationToken.None);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(503);
        (await conn.ExecuteScalarAsync<bool>("SELECT sync_status = 'error' AND sync_requested_at IS NULL AND win_rate = 53 AND hs_pct = 25 FROM public.player_game_stats WHERE user_id = @owner", new { owner })).Should().BeTrue();
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("retry");
        Body(await LfgStatsSyncEndpoints.SyncMineAsync(Context(owner), Service(jobs), CancellationToken.None)).GetProperty("status").GetString().Should().Be("queued");
    }

    [Theory]
    [InlineData("draft", false)]
    [InlineData("paused", false)]
    [InlineData("closed", false)]
    [InlineData("published", true)]
    public async Task HiddenOrExpiredListings_CannotBeRefreshedByOtherUsers(string status, bool expired)
    {
        var (owner, listing) = await Listing(status, expired);
        var viewer = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(viewer);
        var jobs = Jobs();
        var result = await LfgStatsSyncEndpoints.SyncListingAsync(listing, Context(viewer), Db, Service(jobs), CancellationToken.None);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(404);
        jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
        using var client = factory.CreateAuthenticatedClient(viewer);
        (await client.GetAsync($"/api/competitive/lfg/{listing}/stats-sync")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var ownerClient = factory.CreateAuthenticatedClient(owner);
        (await ownerClient.GetAsync($"/api/competitive/lfg/{listing}/stats-sync")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnonymousUnlinkedAndDeletedRequests_CannotQueueWork()
    {
        var (owner, listing) = await Listing();
        using var anonymous = factory.CreateClient();
        (await anonymous.PostAsync($"/api/competitive/lfg/{listing}/sync-stats", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var jobs = Jobs();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("DELETE FROM public.riot_accounts WHERE user_id = @owner", new { owner });
        ((IStatusCodeHttpResult)await LfgStatsSyncEndpoints.SyncListingAsync(listing, Context(owner), Db, Service(jobs), CancellationToken.None)).StatusCode.Should().Be(404);
        ((IStatusCodeHttpResult)await LfgStatsSyncEndpoints.SyncMineAsync(Context(owner), Service(jobs), CancellationToken.None)).StatusCode.Should().Be(400);
        ((IStatusCodeHttpResult)await LfgStatsSyncEndpoints.SyncListingAsync(Guid.NewGuid(), Context(owner), Db, Service(jobs), CancellationToken.None)).StatusCode.Should().Be(404);
        jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    [Fact]
    public async Task ActiveWorkerIsNotDuplicated_AndAbandonedPendingCanBeRetried()
    {
        var (owner, _) = await Listing();
        var jobs = Jobs();
        await using var conn = factory.Seeder.OpenConnection();
        var key = $"valorant-stats:{owner}";
        await conn.ExecuteAsync("SELECT pg_advisory_lock(hashtextextended(@key, 0))", new { key });
        try
        {
            (await Service(jobs).RequestAsync(owner, CancellationToken.None)).Status.Should().Be("syncing");
            jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
        }
        finally { await conn.ExecuteAsync("SELECT pg_advisory_unlock(hashtextextended(@key, 0))", new { key }); }
        await conn.ExecuteAsync("""
            INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, sync_status, updated_at)
            VALUES (@owner, 'valorant', @puuid, 'pending', now() - interval '11 minutes')
            """, new { owner, puuid = owner.ToString() });
        (await Service(jobs).RequestAsync(owner, CancellationToken.None)).Status.Should().Be("queued");
        jobs.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    [Fact]
    public async Task RecentPartialSnapshot_IsServedForAnHour_WithoutNewJobs()
    {
        var (owner, listing) = await Listing();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, sync_status, synced_at, win_rate, last_act_id)
            VALUES (@owner, 'valorant', @puuid, 'partial', now() - interval '59 minutes', 53, 'current')
            """, new { owner, puuid = owner.ToString() });
        var jobs = Jobs();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = Body(await LfgStatsSyncEndpoints.SyncListingAsync(listing, Context(Guid.NewGuid()), Db, Service(jobs), CancellationToken.None));
            result.GetProperty("status").GetString().Should().Be("cached");
            result.GetProperty("retry_after_seconds").GetInt32().Should().BeInRange(1, 65);
        }
        jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
        (await conn.ExecuteScalarAsync<decimal>("SELECT win_rate FROM public.player_game_stats WHERE user_id = @owner", new { owner })).Should().Be(53);
    }
}
