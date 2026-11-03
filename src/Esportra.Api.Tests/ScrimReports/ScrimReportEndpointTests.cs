using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Esportra.Api.Services.ProviderMatches;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;
using static Esportra.Api.Tests.ProviderMatches.ValorantMatchFixture;

namespace Esportra.Api.Tests.ScrimReports;

[Collection(IntegrationCollection.Name)]
public sealed class ScrimReportEndpointTests(ApiFactory factory)
{
    private sealed record World(string Prefix, Guid[] Users, Guid A, Guid O);

    private static readonly Round[] Rounds =
    [
        new(0, "Blue", "Attacker", [new Kill("p1", "p6", 5_000, 5_000), new Kill("p7", "p2", 8_000, 8_000)], Planter: "p1",
            Damage: [new Dmg("p1", "p6", 150, 1, 0, 0), new Dmg("p7", "p2", 120, 0, 1, 0)]),
        new(1, "Red", "Defender", [new Kill("p6", "p1", 4_000, 100_000)], Damage: [new Dmg("p6", "p1", 140, 0, 1, 0)]),
    ];

    /// <summary>users[0] owns team A (staff), users[1..4] are plain members; users[5] owns team O. One verified, confirmed scrim map.</summary>
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
        var matchId = Guid.NewGuid().ToString();
        var game = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.scrim_session_games (session_id, game_number, status, map_name, team1_score, team2_score, winner_slot, source, riot_match_id, reported_by_team_id)
            VALUES (@s, 1, 'confirmed', 'Ascent', 13, 7, 1, 'riot', @m, @a) RETURNING id
            """, new { s = session, m = matchId, a });
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, new ProviderMatchRegistration(ProviderMatchContext.ScrimGame, game, null, 1, matchId, a, o, 13, 7, "Ascent"));
        var payload = Regex.Replace(Build(matchId: matchId, rounds: Rounds).GetRawText(), "\"p(\\d+)\"", m => $"\"{prefix}p{m.Groups[1].Value}\"");
        await ProviderMatchIngest.ApplyAsync(conn, reg!.AssociationId, payload, "eu", true);
        return new World(prefix, users, a, o);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(Guid viewer, string path)
    {
        var response = await factory.CreateAuthenticatedClient(viewer).GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.StatusCode == HttpStatusCode.OK ? JsonDocument.Parse(text).RootElement : default);
    }

    [Fact]
    public async Task Staff_see_the_team_report_with_every_player()
    {
        var w = await SeedAsync();
        var (status, body) = await GetAsync(w.Users[0], $"/api/scrims/teams/{w.A}/report");
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("staff").GetBoolean().Should().BeTrue();
        var report = body.GetProperty("report");
        report.GetProperty("sample").GetProperty("maps").GetInt32().Should().Be(1);
        report.GetProperty("players").GetArrayLength().Should().Be(5);
        report.GetProperty("series").GetArrayLength().Should().Be(1);
        report.GetProperty("wins").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_regular_member_sees_the_team_report_but_only_their_own_player_row()
    {
        var w = await SeedAsync();
        var (status, body) = await GetAsync(w.Users[2], $"/api/scrims/teams/{w.A}/report");
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("staff").GetBoolean().Should().BeFalse();
        var players = body.GetProperty("report").GetProperty("players");
        players.GetArrayLength().Should().Be(1);
        players[0].GetProperty("user_id").GetGuid().Should().Be(w.Users[2]);
        body.GetProperty("report").GetProperty("wins").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Staff_open_any_players_report_and_a_member_only_their_own()
    {
        var w = await SeedAsync();
        var (staffStatus, staffBody) = await GetAsync(w.Users[0], $"/api/scrims/teams/{w.A}/players/{w.Users[1]}/report");
        staffStatus.Should().Be(HttpStatusCode.OK);
        staffBody.GetProperty("has_data").GetBoolean().Should().BeTrue();
        staffBody.GetProperty("report").GetProperty("summary").GetProperty("maps").GetInt32().Should().Be(1);
        staffBody.GetProperty("report").GetProperty("series").GetArrayLength().Should().Be(1);

        (await GetAsync(w.Users[2], $"/api/scrims/teams/{w.A}/players/{w.Users[1]}/report")).Status.Should().Be(HttpStatusCode.Forbidden);
        var (ownStatus, ownBody) = await GetAsync(w.Users[2], $"/api/scrims/teams/{w.A}/players/{w.Users[2]}/report");
        ownStatus.Should().Be(HttpStatusCode.OK);
        ownBody.GetProperty("has_data").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_player_without_maps_says_so_and_strangers_and_unknown_teams_are_refused()
    {
        var w = await SeedAsync();
        var (status, body) = await GetAsync(w.Users[0], $"/api/scrims/teams/{w.A}/players/{Guid.NewGuid()}/report");
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("has_data").GetBoolean().Should().BeFalse();
        body.GetProperty("report").ValueKind.Should().Be(JsonValueKind.Null);

        var stranger = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(stranger);
        (await GetAsync(stranger, $"/api/scrims/teams/{w.A}/report")).Status.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(w.Users[0], $"/api/scrims/teams/{Guid.NewGuid()}/report")).Status.Should().Be(HttpStatusCode.NotFound);
        (await factory.CreateClient().GetAsync($"/api/scrims/teams/{w.A}/report")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_other_team_gets_its_own_view_and_no_puuid_ever_leaves_the_server()
    {
        var w = await SeedAsync();
        var response = await factory.CreateAuthenticatedClient(w.Users[5]).GetAsync($"/api/scrims/teams/{w.O}/report");
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        text.Should().NotContain($"{w.Prefix}p");
        JsonDocument.Parse(text).RootElement.GetProperty("report").GetProperty("losses").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Every_member_can_read_the_team_trends_and_strangers_cannot()
    {
        var w = await SeedAsync();
        var (status, body) = await GetAsync(w.Users[3], $"/api/scrims/teams/{w.A}/trends");
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("has_data").GetBoolean().Should().BeTrue();
        body.GetProperty("trends").GetProperty("series").GetArrayLength().Should().Be(1);
        body.GetProperty("trends").GetProperty("windows").GetArrayLength().Should().Be(3);

        var stranger = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(stranger);
        (await GetAsync(stranger, $"/api/scrims/teams/{w.A}/trends")).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_heatmap_lists_the_teams_recorded_event_positions_by_map_kind_and_side()
    {
        var w = await SeedAsync();
        var (status, body) = await GetAsync(w.Users[3], $"/api/scrims/teams/{w.A}/heatmap?kind=deaths&map=Ascent");
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("available_maps")[0].GetProperty("map").GetString().Should().Be("Ascent");
        var points = body.GetProperty("points");
        points.GetArrayLength().Should().Be(2);
        (points[0].GetProperty("x").GetInt32(), points[0].GetProperty("y").GetInt32()).Should().Be((100, 200));

        (await GetAsync(w.Users[3], $"/api/scrims/teams/{w.A}/heatmap?kind=deaths&side=defense")).Body.GetProperty("points").GetArrayLength().Should().Be(0);
        (await GetAsync(w.Users[3], $"/api/scrims/teams/{w.A}/heatmap?kind=deaths&map=Nowhere")).Body.GetProperty("maps").GetInt32().Should().Be(0);
        var defaulted = (await GetAsync(w.Users[3], $"/api/scrims/teams/{w.A}/heatmap?kind=deaths")).Body;
        defaulted.GetProperty("map").GetString().Should().Be("Ascent");   // no map named: the most-played one, never all maps at once
        defaulted.GetProperty("points").GetArrayLength().Should().Be(2);
        (await GetAsync(w.Users[3], $"/api/scrims/teams/{w.A}/heatmap?kind=bogus")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync(w.Users[3], $"/api/scrims/teams/{w.A}/heatmap?side=middle")).Status.Should().Be(HttpStatusCode.BadRequest);

        var stranger = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(stranger);
        (await GetAsync(stranger, $"/api/scrims/teams/{w.A}/heatmap")).Status.Should().Be(HttpStatusCode.Forbidden);
        text(await factory.CreateAuthenticatedClient(w.Users[3]).GetStringAsync($"/api/scrims/teams/{w.A}/heatmap?kind=deaths")).Should().NotContain($"{w.Prefix}p");
    }

    [Fact]
    public async Task The_fights_view_gives_every_fight_with_spacing_for_team_members_only_and_never_a_puuid()
    {
        var w = await SeedAsync();
        var (status, body) = await GetAsync(w.Users[3], $"/api/scrims/teams/{w.A}/fights");
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("map").GetString().Should().Be("Ascent");
        var fights = body.GetProperty("fights");
        fights.GetArrayLength().Should().Be(3);
        fights.EnumerateArray().Count(f => f.GetProperty("won").GetBoolean()).Should().Be(1);
        (await GetAsync(w.Users[3], $"/api/scrims/teams/{w.A}/fights?side=defense")).Body.GetProperty("fights").GetArrayLength().Should().Be(0);   // both rounds were played attacking
        (await GetAsync(w.Users[3], $"/api/scrims/teams/{w.A}/fights?side=attack")).Body.GetProperty("fights").GetArrayLength().Should().Be(3);
        (await GetAsync(w.Users[3], $"/api/scrims/teams/{w.A}/fights?side=middle")).Status.Should().Be(HttpStatusCode.BadRequest);

        var stranger = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(stranger);
        (await GetAsync(stranger, $"/api/scrims/teams/{w.A}/fights")).Status.Should().Be(HttpStatusCode.Forbidden);
        (await factory.CreateAuthenticatedClient(w.Users[3]).GetStringAsync($"/api/scrims/teams/{w.A}/fights")).Should().NotContain($"{w.Prefix}p");
    }

    private static string text(string s) => s;
}
