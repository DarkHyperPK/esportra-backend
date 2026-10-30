using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Services;
using Esportra.Api.Tests.Infrastructure;
using Esportra.Infrastructure.Integrations;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

[Collection(IntegrationCollection.Name)]
public sealed class ValorantHeadshotRecoveryIntegrationTests(ApiFactory factory)
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task LegacyHits_AreRecoveredWithoutNewMatches_WithoutChangingCompetitiveTotals(int aggregationVersion)
    {
        var user = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(user);
        using var ownerClient = factory.CreateAuthenticatedClient(user);
        var puuid = user.ToString();
        var now = DateTimeOffset.UtcNow;
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@user, @puuid, 'Player', 'TAG');
            INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, last_act_id, last_match_id,
                aggregation_version, matches_analyzed, win_count, loss_count, total_kills, total_deaths,
                total_assists, total_score, kd_ratio, win_rate, headshot_data_complete, updated_at, sync_status, synced_at, popular_agents)
            VALUES (@user, 'valorant', @puuid, 'current', 'newest', @aggregationVersion, 2, 2, 0, 20, 10, 6, 1000, 2, 100, FALSE, now(), 'partial', NULL,
                '[{"agent_id":"agent","games_played":2,"wins":2,"kills":20,"deaths":10,"assists":6,"kd_ratio":2}]'::jsonb);
            INSERT INTO public.valorant_player_matches (user_id, provider_player_id, match_id, act_id, started_at,
                queue_id, agent_id, won, kills, deaths, assists, score, headshots, shots, headshot_data_complete, match_data_version)
            VALUES (@user, @puuid, 'newest', 'current', @newest, 'competitive', 'agent', TRUE, 10, 5, 3, 500, 0, 0, FALSE, 2),
                   (@user, @puuid, 'older', 'current', @older, 'competitive', 'agent', TRUE, 10, 5, 3, 500, 0, 0, FALSE, 2);
            """, new { user, puuid, aggregationVersion, newest = now.AddMinutes(-1).UtcDateTime, older = now.AddMinutes(-2).UtcDateTime });
        var handler = new DetailsHandler(puuid, now);
        var riot = Client(handler);
        // The older ID disappeared from Riot's list but remains recoverable in our archive.
        var list = JsonSerializer.Serialize(new { history = new[] { new { matchId = "newest", gameStartTimeMillis = now.AddMinutes(-1).ToUnixTimeMilliseconds(), queueId = "competitive" } } });
        (await ValorantMatchArchive.SyncAsync(conn, riot, user, puuid, "eu", list, [new("current", now.AddDays(-10), true)], CancellationToken.None)).Should().BeTrue();
        handler.Requests.Should().Be(2);
        await ValorantHeadshotRecovery.RepairAsync(conn, user, puuid, "current");
        var row = await conn.QuerySingleAsync<dynamic>("SELECT hs_pct, headshot_data_complete, matches_analyzed, win_count, kd_ratio, win_rate FROM public.player_game_stats WHERE user_id = @user AND game_slug = 'valorant'", new { user });
        ((decimal?)row.hs_pct).Should().Be(20m);
        ((bool)row.headshot_data_complete).Should().BeTrue();
        ((int)row.matches_analyzed).Should().Be(2);
        ((int)row.win_count).Should().Be(2);
        ((decimal)row.kd_ratio).Should().Be(2m);
        ((decimal)row.win_rate).Should().Be(100m);
        var response = await ownerClient.GetAsync("/api/competitive/game-stats/me?game=valorant");
        response.EnsureSuccessStatusCode();
        var visibleStats = await response.Content.ReadFromJsonAsync<JsonElement>();
        visibleStats.GetProperty("hs_pct").GetDecimal().Should().Be(20m);
        // Recovery must survive the next incremental sync of a legacy aggregate.
        handler.IncludeNextMatch = true;
        (await conn.ExecuteScalarAsync<bool>("SELECT synced_at IS NULL AND sync_requested_at IS NULL FROM public.player_game_stats WHERE user_id = @user", new { user }))
            .Should().BeTrue("reading recent stored stats must not enqueue uncontrolled provider work");
        await SyncServiceAsync(user, riot, handler);
        var afterNext = await conn.QuerySingleAsync<dynamic>("SELECT hs_pct, matches_analyzed, aggregation_version FROM public.player_game_stats WHERE user_id = @user", new { user });
        ((decimal?)afterNext.hs_pct).Should().Be(20m);
        ((int)afterNext.matches_analyzed).Should().Be(3);
        ((int)afterNext.aggregation_version).Should().Be(aggregationVersion);
        var detailRequests = handler.Requests;
        await SyncServiceAsync(user, riot, handler);
        handler.Requests.Should().Be(detailRequests, "a duplicate job must reuse the one-hour snapshot");
        await conn.ExecuteAsync("UPDATE public.player_game_stats SET headshot_data_complete = FALSE, hs_pct = NULL WHERE user_id = @user; UPDATE public.riot_accounts SET puuid = @relinked WHERE user_id = @user", new { user, relinked = Guid.NewGuid().ToString() });
        await ValorantHeadshotRecovery.RepairAsync(conn, user, puuid, "current");
        (await conn.ExecuteScalarAsync<decimal?>("SELECT hs_pct FROM public.player_game_stats WHERE user_id = @user", new { user })).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingHits_AreDeferred_ThenRecoveredAfterRetryBecomesDue(bool manualRetry)
    {
        var user = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(user);
        var puuid = user.ToString();
        var now = DateTimeOffset.UtcNow;
        await using var conn = factory.Seeder.OpenConnection();
        var handler = new DetailsHandler(puuid, now) { Complete = false };
        var riot = Client(handler);
        var list = JsonSerializer.Serialize(new { history = new[] { new { matchId = "newest", gameStartTimeMillis = now.AddMinutes(-1).ToUnixTimeMilliseconds(), queueId = "competitive" } } });
        ValorantMatchArchive.Act[] acts = [new("current", now.AddDays(-10), true)];
        (await ValorantMatchArchive.SyncAsync(conn, riot, user, puuid, "eu", list, acts, CancellationToken.None)).Should().BeFalse();
        (await ValorantMatchArchive.SyncAsync(conn, riot, user, puuid, "eu", list, acts, CancellationToken.None)).Should().BeFalse();
        handler.Requests.Should().Be(1);
        (await conn.ExecuteScalarAsync<bool>("SELECT retry_after > now() FROM public.valorant_match_backlog WHERE user_id = @user", new { user })).Should().BeTrue();
        handler.Complete = true;
        if (!manualRetry) await conn.ExecuteAsync("UPDATE public.valorant_match_backlog SET retry_after = now() - interval '1 second' WHERE user_id = @user", new { user });
        (await ValorantMatchArchive.SyncAsync(conn, riot, user, puuid, "eu", list, acts, CancellationToken.None, manualRetry)).Should().BeTrue();
        handler.Requests.Should().Be(2);
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM public.valorant_match_backlog WHERE user_id = @user", new { user })).Should().Be(0);
    }

    private static RiotApiClient Client(HttpMessageHandler handler) => new(new HttpClient(handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Riot:ApiKey"] = "integration-test" }).Build());

    private async Task SyncServiceAsync(Guid user, RiotApiClient riot, HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        using var provider = services.BuildServiceProvider();
        var db = Substitute.For<Esportra.Contracts.Database.IDbConnectionFactory>();
        db.CreateConnection().Returns(_ => factory.Seeder.OpenConnection());
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        var service = new ValorantStatsSyncService(riot, provider.GetRequiredService<HybridCache>(), db, http,
            NullLogger<ValorantStatsSyncService>.Instance);
        await service.SyncForUserAsync(user, CancellationToken.None);
    }

    private sealed class DetailsHandler(string puuid, DateTimeOffset now) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool Complete { get; set; } = true;
        public bool IncludeNextMatch { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("active-shards")) return Json(new { activeShard = "eu" });
            if (request.RequestUri.AbsolutePath == "/v1/seasons") return Json(new { data = new[] { new { uuid = "current", type = "EAresSeasonType::Act", startTime = now.AddDays(-10), endTime = now.AddDays(10) } } });
            if (request.RequestUri.AbsolutePath == "/v1/agents") return Json(new { data = Array.Empty<object>() });
            if (request.RequestUri.AbsolutePath.Contains("matchlists")) return Json(new { history = (IncludeNextMatch ? new[] { "next", "newest", "older" } : ["newest", "older"]).Select(id => new { matchId = id, gameStartTimeMillis = now.AddMinutes(id == "next" ? 0 : id == "older" ? -2 : -1).ToUnixTimeMilliseconds(), queueId = "competitive" }) });
            Requests++;
            var id = request.RequestUri!.Segments.Last();
            var body = new
            {
                matchInfo = new { matchId = id, seasonId = "current", gameStartMillis = now.AddMinutes(id == "next" ? 0 : id == "older" ? -2 : -1).ToUnixTimeMilliseconds(), queueId = "competitive" },
                teams = new[] { new { teamId = "Blue", won = true }, new { teamId = "Red", won = false } },
                players = new[] { new { puuid, teamId = "Blue", characterId = "agent", playerCard = "c86953ca-4c94-c0f7-fd91-c19e03ff2b9f", competitiveTier = 10, stats = new { kills = 10, deaths = 5, assists = 3, score = 500 } } },
                roundResults = Complete ? [new { playerStats = new[] { new { puuid, damage = new[] { new { headshots = 2, bodyshots = 7, legshots = 1 } } } } }] : Array.Empty<object>()
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }

        private static Task<HttpResponseMessage> Json(object body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
    }
}
