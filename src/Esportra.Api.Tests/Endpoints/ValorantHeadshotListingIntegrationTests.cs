using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Services;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Esportra.Contracts.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

[Collection(IntegrationCollection.Name)]
public sealed class ValorantHeadshotListingIntegrationTests(ApiFactory factory)
{
    [Fact]
    public async Task VerifiedAgentCoverage_AndLaterOverallRecovery_AppearOnEveryListingSurface()
    {
        var owner = Guid.NewGuid();
        var viewer = Guid.NewGuid();
        var listing = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(owner);
        await factory.Seeder.SeedAuthUserAsync(viewer);
        await factory.Seeder.SeedFeatureFlagAsync("lfg", true);
        using var ownerClient = factory.CreateAuthenticatedClient(owner);
        using var client = factory.CreateAuthenticatedClient(viewer);
        await SeedPartialAsync(owner, listing);
        await using var conn = factory.Seeder.OpenConnection();
        await ValorantHeadshotRecovery.RepairAsync(conn, owner, owner.ToString(), "current");
        var first = await ReadAsync(client, $"/api/competitive/lfg/{listing}");
        first.GetProperty("hsPct").ValueKind.Should().Be(JsonValueKind.Null);
        using var agents = JsonDocument.Parse(first.GetProperty("popularAgents").GetString()!);
        agents.RootElement[0].GetProperty("hs_pct").GetDecimal().Should().Be(20m);
        agents.RootElement[1].TryGetProperty("hs_pct", out _).Should().BeFalse();
        await conn.ExecuteAsync("""
            UPDATE public.valorant_player_matches SET headshot_data_complete = TRUE, headshots = 3, shots = 10
            WHERE user_id = @owner AND match_id = 'older';
            UPDATE public.player_game_stats SET sync_status = 'partial', synced_at = now() WHERE user_id = @owner
            """, new { owner });
        var jobs = Substitute.For<IBackgroundJobClient>();
        var requests = new ValorantSyncRequestService(factory.Services.GetRequiredService<IDbConnectionFactory>(), jobs,
            NullLogger<ValorantSyncRequestService>.Instance);
        (await requests.RequestAsync(owner, CancellationToken.None)).Status.Should().Be("cached");
        jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
        (await ReadAsync(client, $"/api/competitive/lfg/{listing}")).GetProperty("hsPct").GetDecimal().Should().Be(25m);
        foreach (var (reader, route) in new[] { (client, "/api/competitive/lfg?game=valorant"), (ownerClient, "/api/competitive/lfg/mine") })
        {
            var rows = await ReadAsync(reader, route);
            rows.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == listing).GetProperty("hsPct").GetDecimal().Should().Be(25m);
        }
        (await client.PutAsync($"/api/competitive/lfg/{listing}/saved", null)).EnsureSuccessStatusCode();
        var saved = await ReadAsync(client, "/api/competitive/lfg/saved");
        saved.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == listing).GetProperty("hsPct").GetDecimal().Should().Be(25m);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task SeedPartialAsync(Guid owner, Guid listing)
    {
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@owner, @puuid, 'Player', 'TAG');
            INSERT INTO public.lfg_listings (id, user_id, game_slug, intent, status, expires_at)
            VALUES (@listing, @owner, 'valorant', 'looking_for_team', 'published', now() + interval '1 day');
            INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, last_act_id, last_match_id,
                aggregation_version, matches_analyzed, win_count, loss_count, total_kills, total_deaths,
                total_assists, total_score, headshot_data_complete, updated_at, sync_status, popular_agents)
            VALUES (@owner, 'valorant', @puuid, 'current', 'newest', 3, 2, 2, 0, 20, 10, 6, 1000, FALSE, now(), 'partial',
                '[{"agent_id":"one","games_played":1,"wins":1,"kills":10,"deaths":5,"assists":3,"headshot_data_complete":false,"hs_pct":99},
                  {"agent_id":"two","games_played":1,"wins":1,"kills":10,"deaths":5,"assists":3,"headshot_data_complete":false,"hs_pct":99}]');
            INSERT INTO public.valorant_player_matches (user_id, provider_player_id, match_id, act_id, started_at,
                queue_id, agent_id, won, kills, deaths, assists, score, headshots, shots, headshot_data_complete, match_data_version)
            VALUES (@owner, @puuid, 'newest', 'current', now() - interval '1 minute', 'competitive', 'one', TRUE, 10, 5, 3, 500, 2, 10, TRUE, 3),
                   (@owner, @puuid, 'older', 'current', now() - interval '2 minutes', 'competitive', 'two', TRUE, 10, 5, 3, 500, 0, 0, FALSE, 3)
            """, new { owner, listing, puuid = owner.ToString() });
    }
}
