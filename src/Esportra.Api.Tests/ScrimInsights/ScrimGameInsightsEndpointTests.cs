using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Esportra.Api.Services.ProviderMatches;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;
using static Esportra.Api.Tests.ProviderMatches.ValorantMatchFixture;

namespace Esportra.Api.Tests.ScrimInsights;

[Collection(IntegrationCollection.Name)]
public sealed class ScrimGameInsightsEndpointTests(ApiFactory factory)
{
    private sealed record World(string Prefix, Guid[] Users, Guid A, Guid O, Guid Session);

    private static readonly Round[] Rounds =
    [
        new(0, "Blue", "Attacker", [new Kill("p1", "p6", 5_000, 5_000), new Kill("p7", "p2", 8_000, 8_000)], Planter: "p1",
            Damage: [new Dmg("p1", "p6", 150, 1, 0, 0), new Dmg("p7", "p2", 120, 0, 1, 0)]),
        new(1, "Red", "Defender", [new Kill("p6", "p1", 4_000, 100_000)], Damage: [new Dmg("p6", "p1", 140, 0, 1, 0)]),
    ];

    private async Task<World> SeedAsync()
    {
        var prefix = Guid.NewGuid().ToString("N")[..8];
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
        var session = await conn.QuerySingleAsync<Guid>("INSERT INTO public.scrim_sessions (team1_id, team2_id, game_slug, status) VALUES (@a, @o, 'valorant', 'completed') RETURNING id", new { a, o });
        return new World(prefix, users, a, o, session);
    }

    private async Task<Guid> GameAsync(World w, int number, string source, string? matchId, int s1 = 13, int s2 = 7)
    {
        await using var conn = factory.Seeder.OpenConnection();
        return await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.scrim_session_games (session_id, game_number, status, map_name, team1_score, team2_score, winner_slot, source, riot_match_id, reported_by_team_id)
            VALUES (@s, @n, 'confirmed', 'Ascent', @s1, @s2, @winner, @source, @riot, @a) RETURNING id
            """, new { s = w.Session, n = number, s1, s2, winner = s1 > s2 ? 1 : 2, source, riot = matchId, a = w.A });
    }

    private async Task RiotAsync(World w, Guid gameId, string matchId)
    {
        await using var conn = factory.Seeder.OpenConnection();
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, new ProviderMatchRegistration(ProviderMatchContext.ScrimGame, gameId, null, 1, matchId, w.A, w.O, 13, 7, "Ascent"));
        var payload = Regex.Replace(Build(matchId: matchId, rounds: Rounds).GetRawText(), "\"p(\\d+)\"", m => $"\"{w.Prefix}p{m.Groups[1].Value}\"");
        await ProviderMatchIngest.ApplyAsync(conn, reg!.AssociationId, payload, "eu", true);
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(Guid viewer, Guid session, int n, Guid? team = null)
    {
        var url = $"/api/scrims/sessions/{session}/games/{n}/insights" + (team is null ? "" : $"?team_id={team}");
        var response = await factory.CreateAuthenticatedClient(viewer).GetAsync(url);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_member_gets_the_full_report_without_any_puuid()
    {
        var w = await SeedAsync();
        var matchId = Guid.NewGuid().ToString();
        await RiotAsync(w, await GameAsync(w, 1, "riot", matchId), matchId);

        var (status, body) = await GetAsync(w.Users[1], w.Session, 1);

        status.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain($"{w.Prefix}p", "PUUIDs never leave the server");
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("tier").GetString().Should().Be("full");
        json.GetProperty("map").GetString().Should().Be("Ascent");
        json.GetProperty("result").GetString().Should().Be("win");
        var report = json.GetProperty("report");
        report.GetProperty("players").GetArrayLength().Should().Be(10);
        report.GetProperty("players").EnumerateArray().Count(p => p.GetProperty("mine").GetBoolean()).Should().Be(5);
        report.GetProperty("rounds").GetArrayLength().Should().Be(2);
        report.GetProperty("kills").GetArrayLength().Should().Be(3);
        report.GetProperty("kills")[0].GetProperty("type").GetString().Should().Be("Weapon");
        report.GetProperty("objectives").GetProperty("by_player").EnumerateArray().Single().GetProperty("plants").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task The_other_team_sees_the_same_map_from_its_own_side()
    {
        var w = await SeedAsync();
        var matchId = Guid.NewGuid().ToString();
        await RiotAsync(w, await GameAsync(w, 1, "riot", matchId), matchId);

        var (status, body) = await GetAsync(w.Users[6], w.Session, 1);

        status.Should().Be(HttpStatusCode.OK, body);
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("my_team").GetProperty("id").GetGuid().Should().Be(w.O);
        json.GetProperty("result").GetString().Should().Be("loss");
        json.GetProperty("report").GetProperty("rounds")[0].GetProperty("won").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_map_without_riot_data_says_so_and_returns_no_sections()
    {
        var w = await SeedAsync();
        await GameAsync(w, 1, "manual", null);

        var (status, body) = await GetAsync(w.Users[1], w.Session, 1);

        status.Should().Be(HttpStatusCode.OK, body);
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("tier").GetString().Should().Be("score_only");
        json.GetProperty("reason").GetString().Should().Contain("not reported from a Riot match");
        json.GetProperty("report").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Strangers_are_refused_and_unknown_maps_are_not_found()
    {
        var w = await SeedAsync();
        await GameAsync(w, 1, "manual", null);
        var stranger = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(stranger);

        (await GetAsync(stranger, w.Session, 1)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(w.Users[1], w.Session, 9)).Status.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync(w.Users[1], w.Session, 1, Guid.NewGuid())).Status.Should().Be(HttpStatusCode.BadRequest);
        (await factory.CreateClient().GetAsync($"/api/scrims/sessions/{w.Session}/games/1/insights")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_timeline_gives_rounds_with_kills_for_members_and_a_reason_when_there_is_no_riot_data()
    {
        var w = await SeedAsync();
        var matchId = Guid.NewGuid().ToString();
        await RiotAsync(w, await GameAsync(w, 1, "riot", matchId), matchId);
        await GameAsync(w, 2, "manual", null);

        var ok = await factory.CreateAuthenticatedClient(w.Users[1]).GetAsync($"/api/scrims/sessions/{w.Session}/games/1/timeline");
        var text = await ok.Content.ReadAsStringAsync();
        ok.StatusCode.Should().Be(HttpStatusCode.OK, text);
        text.Should().NotContain($"{w.Prefix}p", "PUUIDs never leave the server");
        var json = JsonDocument.Parse(text).RootElement;
        json.GetProperty("map").GetString().Should().Be("Ascent");
        var timeline = json.GetProperty("timeline");
        timeline.GetProperty("players").GetArrayLength().Should().Be(10);
        timeline.GetProperty("rounds").GetArrayLength().Should().Be(2);
        timeline.GetProperty("rounds")[0].GetProperty("kills").GetArrayLength().Should().Be(2);

        var none = JsonDocument.Parse(await factory.CreateAuthenticatedClient(w.Users[1]).GetStringAsync($"/api/scrims/sessions/{w.Session}/games/2/timeline")).RootElement;
        none.GetProperty("timeline").ValueKind.Should().Be(JsonValueKind.Null);
        none.GetProperty("reason").GetString().Should().Contain("not reported from a Riot match");

        var stranger = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(stranger);
        (await factory.CreateAuthenticatedClient(stranger).GetAsync($"/api/scrims/sessions/{w.Session}/games/1/timeline")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await factory.CreateAuthenticatedClient(w.Users[1]).GetAsync($"/api/scrims/sessions/{w.Session}/games/9/timeline")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
