using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Services;
using Esportra.Api.Tests.Infrastructure;
using Esportra.Infrastructure.Integrations;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

[Collection(IntegrationCollection.Name)]
public sealed class ValorantMatchArchiveIntegrationTests(ApiFactory factory) : IAsyncLifetime
{
    public async Task InitializeAsync() => await factory.Seeder.SeedFeatureFlagAsync("lfg", enabled: true);
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Sync_ResumesAfterRateLimitEvenWhenMatchDisappearsFromRiotList_AndIsPrivate()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(owner);
        await factory.Seeder.SeedAuthUserAsync(other);
        var puuid = Guid.NewGuid().ToString();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@owner, @puuid, 'Player', 'TAG')", new { owner, puuid });
        var now = DateTimeOffset.UtcNow;
        var acts = new[] {
            new ValorantMatchArchive.Act("current", now.AddDays(-10), true),
        };
        // A previously archived match may predate the player-card field.
        await conn.ExecuteAsync("""
            INSERT INTO public.valorant_player_matches
                (user_id, provider_player_id, match_id, act_id, started_at, queue_id, kills, deaths, assists, score, match_data_version)
            VALUES (@owner, @puuid, 'a-current', 'current', @started, 'competitive', 10, 5, 3, 500, 1)
            """, new { owner, puuid, started = now.AddDays(-1).UtcDateTime });
        var handler = new MatchHandler(puuid, now);
        var riot = new RiotApiClient(new HttpClient(handler), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Riot:ApiKey"] = "test-key" }).Build());
        var list = JsonSerializer.Serialize(new
        {
            history = new[] {
            new { matchId = "a-current", gameStartTimeMillis = now.AddDays(-1).ToUnixTimeMilliseconds(), queueId = "competitive" },
            new { matchId = "b-current", gameStartTimeMillis = now.AddDays(-2).ToUnixTimeMilliseconds(), queueId = "competitive" },
        }
        });
        (await ValorantMatchArchive.SyncAsync(conn, riot, owner, puuid, "ap", list, acts, CancellationToken.None)).Should().BeFalse();
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.valorant_player_matches WHERE user_id = @owner", new { owner })).Should().Be(1);
        var currentOnly = JsonSerializer.Serialize(new
        {
            history = new[] {
            new { matchId = "a-current", gameStartTimeMillis = now.AddDays(-1).ToUnixTimeMilliseconds(), queueId = "competitive" },
        }
        });
        (await ValorantMatchArchive.SyncAsync(conn, riot, owner, puuid, "ap", currentOnly, acts, CancellationToken.None)).Should().BeTrue();
        (await ValorantMatchArchive.SyncAsync(conn, riot, owner, puuid, "ap", currentOnly, acts, CancellationToken.None)).Should().BeTrue();
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.valorant_player_matches WHERE user_id = @owner", new { owner })).Should().Be(2);
        handler.CurrentRequests.Should().Be(1);
        (await conn.ExecuteScalarAsync<string>("SELECT agent_id FROM public.valorant_player_matches WHERE user_id = @owner AND match_id = 'a-current'", new { owner }))
            .Should().Be("agent");
        (await conn.ExecuteScalarAsync<string>("SELECT player_card_id FROM public.valorant_player_matches WHERE user_id = @owner AND match_id = 'a-current'", new { owner }))
            .Should().Be("c86953ca-4c94-c0f7-fd91-c19e03ff2b9f");
        handler.PreviousRequests.Should().Be(2);
        await conn.ExecuteAsync("""
            INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, sync_status)
            VALUES (@owner, 'valorant', @puuid, 'complete')
            """, new { owner, puuid });
        var ownerClient = factory.CreateAuthenticatedClient(owner);
        var stats = await (await ownerClient.GetAsync("/api/competitive/game-stats/me?game=valorant")).Content.ReadFromJsonAsync<JsonElement>();
        var savedActs = stats.GetProperty("act_stats").EnumerateArray().ToArray();
        savedActs.Should().HaveCount(1);
        savedActs[0].GetProperty("competitive_matches").GetInt32().Should().Be(2);
        savedActs[0].GetProperty("popular_agents")[0].GetProperty("games_played").GetInt32().Should().Be(2);
        savedActs[0].TryGetProperty("provider_player_id", out _).Should().BeFalse();
        (await ownerClient.GetAsync("/api/competitive/game-stats/valorant/matches?act=previous")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var current = await (await ownerClient.GetAsync("/api/competitive/game-stats/valorant/matches")).Content.ReadFromJsonAsync<JsonElement>();
        current.GetArrayLength().Should().Be(2);
        current[0].TryGetProperty("providerPlayerId", out _).Should().BeFalse();
        var otherClient = factory.CreateAuthenticatedClient(other);
        var otherMatches = await (await otherClient.GetAsync("/api/competitive/game-stats/valorant/matches")).Content.ReadFromJsonAsync<JsonElement>();
        otherMatches.GetArrayLength().Should().Be(0);
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("SET LOCAL ROLE authenticated", transaction: tx);
        var action = async () => await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.valorant_player_matches", transaction: tx);
        await action.Should().ThrowAsync<Exception>();
    }

    private sealed class MatchHandler(string puuid, DateTimeOffset now) : HttpMessageHandler
    {
        public int CurrentRequests { get; private set; }
        public int PreviousRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var id = request.RequestUri!.Segments.Last();
            var previous = id == "b-current";
            if (previous) PreviousRequests++; else CurrentRequests++;
            if (previous && PreviousRequests == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            var body = new
            {
                matchInfo = new
                {
                    matchId = id,
                    seasonId = "current",
                    gameStartMillis = (previous ? now.AddDays(-2) : now.AddDays(-1)).ToUnixTimeMilliseconds(),
                    queueId = "competitive"
                },
                teams = new[] { new { teamId = "Blue", won = true }, new { teamId = "Red", won = false } },
                players = new[] { new { puuid, teamId = "Blue", characterId = "agent", playerCard = "c86953ca-4c94-c0f7-fd91-c19e03ff2b9f", competitiveTier = 10,
                    stats = new { kills = 10, deaths = 5, assists = 3, score = 500 } } },
                roundResults = new[] { new { playerStats = new[] { new { puuid, damage = new[] { new { headshots = 2, bodyshots = 7, legshots = 1 } } } } } }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
}
