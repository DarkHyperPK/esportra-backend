using System.Text.RegularExpressions;
using Dapper;
using Esportra.Api.Services;
using Esportra.Api.Services.ProviderMatches;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;
using static Esportra.Api.Tests.ProviderMatches.ValorantMatchFixture;

namespace Esportra.Api.Tests.Scrims;

[Collection(IntegrationCollection.Name)]
public sealed class MapPoolLearningTests(ApiFactory factory)
{
    [Fact]
    public void LearnFromPool_NamesNewCodes_ButNeverOverridesBuiltInOrEarlierNames()
    {
        ValorantMaps.ForgetLearned();
        ValorantMaps.LearnFromPool([("PoolOnlyCode", "Pool Summit"), ("Triad", "Wrong"), ("PoolOnlyCode", "Other")]);
        ValorantMaps.DisplayName("/Game/Maps/PoolOnlyCode/PoolOnlyCode").Should().Be("Pool Summit");
        ValorantMaps.DisplayName("Triad").Should().Be("Haven");
        ValorantMaps.CompetitiveNames.Should().Contain("Pool Summit");
        ValorantMaps.ForgetLearned();
    }

    [Fact]
    public async Task LearnFromPoolAsync_NamesAMapRiotsListLacksFromTheVerifiedGameAndThePool()
    {
        ValorantMaps.ForgetLearned();
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var code = "Pool" + prefix;
        var mapName = "Pool Map " + prefix;
        var users = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var u in users) await factory.Seeder.SeedAuthUserAsync(u);
        var a = await factory.Seeder.SeedTeamAsync(users[0], "Alpha " + prefix);
        var o = await factory.Seeder.SeedTeamAsync(users[5], "Omega " + prefix);
        await using var conn = factory.Seeder.OpenConnection();
        for (var i = 0; i < 10; i++)
        {
            await conn.ExecuteAsync("INSERT INTO public.team_members (team_id, user_id, role) VALUES (@t, @u, 'member') ON CONFLICT (team_id, user_id) DO NOTHING", new { t = i < 5 ? a : o, u = users[i] });
            await conn.ExecuteAsync("INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@u, @p, @n, 'EUW')", new { u = users[i], p = $"{prefix}p{i + 1}", n = $"Player{i + 1}" });
        }
        await conn.ExecuteAsync("INSERT INTO public.game_maps (game, map_name, is_active) VALUES ('Valorant', @n, TRUE)", new { n = mapName });
        var session = await conn.QuerySingleAsync<Guid>("INSERT INTO public.scrim_sessions (team1_id, team2_id, game_slug, status) VALUES (@a, @o, 'valorant', 'completed') RETURNING id", new { a, o });
        var matchId = Guid.NewGuid().ToString();
        var game = await conn.QuerySingleAsync<Guid>(
            "INSERT INTO public.scrim_session_games (session_id, game_number, status, map_name, team1_score, team2_score, winner_slot, source, riot_match_id, reported_by_team_id) VALUES (@s, 1, 'confirmed', @n, 13, 7, 1, 'riot', @m, @a) RETURNING id",
            new { s = session, n = mapName, m = matchId, a });
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, new ProviderMatchRegistration(ProviderMatchContext.ScrimGame, game, null, 1, matchId, a, o, 13, 7, mapName));
        var payload = Regex.Replace(Build(matchId: matchId, mapId: $"/Game/Maps/{code}/{code}").GetRawText(), "\"p(\\d+)\"", m => $"\"{prefix}p{m.Groups[1].Value}\"");
        await ProviderMatchIngest.ApplyAsync(conn, reg!.AssociationId, payload, "eu", true);

        ValorantMaps.DisplayName(code).Should().BeNull();
        var refresher = new ValorantMapCatalogRefresher(null!, factory.Db, Microsoft.Extensions.Logging.Abstractions.NullLogger<ValorantMapCatalogRefresher>.Instance);
        await refresher.LearnFromPoolAsync(CancellationToken.None);

        ValorantMaps.DisplayName($"/Game/Maps/{code}/{code}").Should().Be(mapName);
        ValorantMaps.ForgetLearned();
    }
}
