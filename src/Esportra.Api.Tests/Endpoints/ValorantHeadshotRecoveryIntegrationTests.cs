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
public sealed class ValorantHeadshotRecoveryIntegrationTests(ApiFactory factory)
{
    [Fact]
    public async Task LegacyHits_AreRecoveredWithoutNewMatches_WithoutChangingCompetitiveTotals()
    {
        var user = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(user);
        var puuid = user.ToString();
        var now = DateTimeOffset.UtcNow;
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@user, @puuid, 'Player', 'TAG');
            INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, last_act_id, last_match_id,
                aggregation_version, matches_analyzed, win_count, loss_count, total_kills, total_deaths,
                total_assists, total_score, kd_ratio, win_rate, headshot_data_complete, popular_agents)
            VALUES (@user, 'valorant', @puuid, 'current', 'newest', 4, 2, 2, 0, 20, 10, 6, 1000, 2, 100, FALSE,
                '[{"agent_id":"agent","games_played":2,"kd_ratio":2}]'::jsonb);
            INSERT INTO public.valorant_player_matches (user_id, provider_player_id, match_id, act_id, started_at,
                queue_id, agent_id, won, kills, deaths, assists, score, headshots, shots, headshot_data_complete, match_data_version)
            VALUES (@user, @puuid, 'newest', 'current', @newest, 'competitive', 'agent', TRUE, 10, 5, 3, 500, 0, 0, FALSE, 2),
                   (@user, @puuid, 'older', 'current', @older, 'competitive', 'agent', TRUE, 10, 5, 3, 500, 0, 0, FALSE, 2);
            """, new { user, puuid, newest = now.AddMinutes(-1).UtcDateTime, older = now.AddMinutes(-2).UtcDateTime });
        var handler = new DetailsHandler(puuid, now);
        var riot = Client(handler);
        // The older ID disappeared from Riot's list but remains recoverable in our archive.
        var list = JsonSerializer.Serialize(new { history = new[] { new { matchId = "newest", gameStartTimeMillis = now.AddMinutes(-1).ToUnixTimeMilliseconds(), queueId = "competitive" } } });
        (await ValorantMatchArchive.SyncAsync(conn, riot, user, puuid, "eu", list, [new("current", now.AddDays(-10), true)], CancellationToken.None)).Should().BeTrue();
        handler.Requests.Should().Be(2);
        await ValorantHeadshotRecovery.RepairAsync(conn, user, puuid, "current");
        var row = await conn.QuerySingleAsync<dynamic>("SELECT hs_pct, headshot_data_complete, matches_analyzed, win_count, kd_ratio, win_rate FROM public.player_game_stats WHERE user_id = @user AND game_slug = 'valorant'", new { user });
        ((decimal)row.hs_pct).Should().Be(20m);
        ((bool)row.headshot_data_complete).Should().BeTrue();
        ((int)row.matches_analyzed).Should().Be(2);
        ((int)row.win_count).Should().Be(2);
        ((decimal)row.kd_ratio).Should().Be(2m);
        ((decimal)row.win_rate).Should().Be(100m);
        await conn.ExecuteAsync("UPDATE public.player_game_stats SET headshot_data_complete = FALSE, hs_pct = NULL WHERE user_id = @user; UPDATE public.riot_accounts SET puuid = 'relinked' WHERE user_id = @user", new { user });
        await ValorantHeadshotRecovery.RepairAsync(conn, user, puuid, "current");
        (await conn.ExecuteScalarAsync<decimal?>("SELECT hs_pct FROM public.player_game_stats WHERE user_id = @user", new { user })).Should().BeNull();
    }

    [Fact]
    public async Task MissingHits_AreDeferred_ThenRecoveredAfterRetryBecomesDue()
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
        await conn.ExecuteAsync("UPDATE public.valorant_match_backlog SET retry_after = now() - interval '1 second' WHERE user_id = @user", new { user });
        (await ValorantMatchArchive.SyncAsync(conn, riot, user, puuid, "eu", list, acts, CancellationToken.None)).Should().BeTrue();
        handler.Requests.Should().Be(2);
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM public.valorant_match_backlog WHERE user_id = @user", new { user })).Should().Be(0);
    }

    private static RiotApiClient Client(HttpMessageHandler handler) => new(new HttpClient(handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Riot:ApiKey"] = "integration-test" }).Build());

    private sealed class DetailsHandler(string puuid, DateTimeOffset now) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool Complete { get; set; } = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var id = request.RequestUri!.Segments.Last();
            var body = new
            {
                matchInfo = new { matchId = id, seasonId = "current", gameStartMillis = now.AddMinutes(id == "older" ? -2 : -1).ToUnixTimeMilliseconds(), queueId = "competitive" },
                teams = new[] { new { teamId = "Blue", won = true }, new { teamId = "Red", won = false } },
                players = new[] { new { puuid, teamId = "Blue", characterId = "agent", playerCard = "c86953ca-4c94-c0f7-fd91-c19e03ff2b9f", competitiveTier = 10, stats = new { kills = 10, deaths = 5, assists = 3, score = 500 } } },
                roundResults = Complete ? [new { playerStats = new[] { new { puuid, damage = new[] { new { headshots = 2, bodyshots = 7, legshots = 1 } } } } }] : Array.Empty<object>()
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
}
