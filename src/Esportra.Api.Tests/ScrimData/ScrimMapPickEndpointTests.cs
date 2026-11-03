using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.ScrimData;

[Collection(IntegrationCollection.Name)]
public sealed class ScrimMapPickEndpointTests(ApiFactory factory)
{
    private sealed record World(Guid[] Users, Guid A, Guid O, Guid Session, Dictionary<string, string> Maps);

    private async Task<World> SeedAsync(string vetoStatus = "completed")
    {
        var users = Enumerable.Range(0, 2).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var u in users) await factory.Seeder.SeedAuthUserAsync(u);
        var a = await factory.Seeder.SeedTeamAsync(users[0], "Alpha " + Guid.NewGuid().ToString("N")[..6]);
        var o = await factory.Seeder.SeedTeamAsync(users[1], "Omega " + Guid.NewGuid().ToString("N")[..6]);
        await using var conn = factory.Seeder.OpenConnection();
        var maps = new Dictionary<string, string>();
        foreach (var name in new[] { "Lotus", "Bind", "Haven", "Split", "Ascent" })
            maps[name] = (await conn.QuerySingleAsync<Guid>("INSERT INTO public.game_maps (game, map_name) VALUES ('valorant', @n) ON CONFLICT (game, map_name) DO UPDATE SET map_name = EXCLUDED.map_name RETURNING id", new { n = name })).ToString();
        var veto = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.public_veto_sessions (game, best_of, team1_name, team2_name, status, host_token, team1_token, team2_token, selected_map_id, completed_at)
            VALUES ('valorant', 3, 'A', 'B', @status, @h, @a, @b, @decider, now()) RETURNING id
            """,
            new { status = vetoStatus, h = Guid.NewGuid().ToString("N"), a = Guid.NewGuid().ToString("N"), b = Guid.NewGuid().ToString("N"), decider = maps["Ascent"] });
        async Task Action(string side, string type, string map, int n, string? sideChosen = null) =>
            await conn.ExecuteAsync("INSERT INTO public.public_veto_actions (session_id, team_id, team_side, action_type, map_id, action_number, side) VALUES (@v, gen_random_uuid(), @side, @type, @m, @n, @sc)",
                new { v = veto, side, type, m = maps[map], n, sc = sideChosen });
        await Action("team1", "ban", "Haven", 1); await Action("team2", "ban", "Split", 2);
        await Action("team1", "pick", "Lotus", 3); await Action("team2", "pick", "Bind", 4); await Action("team1", "pick_side", "Bind", 5, "attack");
        var session = await conn.QuerySingleAsync<Guid>(
            "INSERT INTO public.scrim_sessions (team1_id, team2_id, game_slug, status, veto_session_id, best_of) VALUES (@a, @o, 'valorant', 'completed', @veto, 3) RETURNING id", new { a, o, veto });
        async Task Game(int n, string map, int s1, int s2) =>
            await conn.ExecuteAsync("INSERT INTO public.scrim_session_games (session_id, game_number, status, map_name, team1_score, team2_score, winner_slot, source, reported_by_team_id) VALUES (@s, @n, 'confirmed', @m, @s1, @s2, @w, 'manual', @a)",
                new { s = session, n, m = map, s1, s2, w = s1 > s2 ? 1 : 2, a });
        await Game(1, "Lotus", 13, 7); await Game(2, "Bind", 8, 13); await Game(3, "Ascent", 13, 11);
        return new World(users, a, o, session, maps);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(Guid viewer, Guid team)
    {
        var response = await factory.CreateAuthenticatedClient(viewer).GetAsync($"/api/scrims/teams/{team}/map-picks");
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.StatusCode == HttpStatusCode.OK ? JsonDocument.Parse(text).RootElement : default);
    }

    [Fact]
    public async Task A_member_sees_what_their_team_and_the_opponent_picked_banned_and_how_it_went()
    {
        var w = await SeedAsync();
        var (status, body) = await GetAsync(w.Users[0], w.A);
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("has_data").GetBoolean().Should().BeTrue();
        var picks = body.GetProperty("picks");
        picks.GetProperty("vetoes").GetInt32().Should().Be(1);
        var maps = picks.GetProperty("maps").EnumerateArray().ToDictionary(m => m.GetProperty("map").GetString()!);
        maps["Lotus"].GetProperty("you_picked").GetInt32().Should().Be(1);
        maps["Bind"].GetProperty("they_picked").GetInt32().Should().Be(1);
        maps["Haven"].GetProperty("you_banned").GetInt32().Should().Be(1);
        maps["Split"].GetProperty("they_banned").GetInt32().Should().Be(1);
        maps["Ascent"].GetProperty("decider").GetInt32().Should().Be(1);
        var byPicker = picks.GetProperty("by_picker").EnumerateArray().ToDictionary(p => p.GetProperty("picked_by").GetString()!);
        (byPicker["you"].GetProperty("wins").GetInt32(), byPicker["opponent"].GetProperty("wins").GetInt32(), byPicker["decider"].GetProperty("wins").GetInt32()).Should().Be((1, 0, 1));
        picks.GetProperty("your_side_choices")[0].GetProperty("side").GetString().Should().Be("attack");
    }

    [Fact]
    public async Task The_other_team_sees_the_same_veto_from_its_own_side()
    {
        var w = await SeedAsync();
        var (status, body) = await GetAsync(w.Users[1], w.O);
        status.Should().Be(HttpStatusCode.OK);
        var maps = body.GetProperty("picks").GetProperty("maps").EnumerateArray().ToDictionary(m => m.GetProperty("map").GetString()!);
        maps["Lotus"].GetProperty("they_picked").GetInt32().Should().Be(1);
        maps["Bind"].GetProperty("you_picked").GetInt32().Should().Be(1);
        maps["Split"].GetProperty("you_banned").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task An_unfinished_veto_is_not_counted_and_a_team_without_vetoes_has_no_data()
    {
        var w = await SeedAsync(vetoStatus: "in_progress");
        var (status, body) = await GetAsync(w.Users[0], w.A);
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("has_data").GetBoolean().Should().BeFalse();
        body.GetProperty("picks").GetProperty("maps").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Strangers_are_refused_unknown_teams_are_not_found_and_anonymous_is_unauthorized()
    {
        var w = await SeedAsync();
        var stranger = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(stranger);
        (await GetAsync(stranger, w.A)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(w.Users[0], Guid.NewGuid())).Status.Should().Be(HttpStatusCode.NotFound);
        (await factory.CreateClient().GetAsync($"/api/scrims/teams/{w.A}/map-picks")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_opponents_endpoint_lists_the_partner_as_a_team_with_meetings_and_the_veto_against_them()
    {
        var w = await SeedAsync();
        var response = await factory.CreateAuthenticatedClient(w.Users[0]).GetAsync($"/api/scrims/teams/{w.A}/opponents");
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("has_data").GetBoolean().Should().BeTrue();
        var opponent = body.GetProperty("opponents")[0];
        opponent.GetProperty("opponent_id").GetGuid().Should().Be(w.O);
        (opponent.GetProperty("maps").GetInt32(), opponent.GetProperty("wins").GetInt32(), opponent.GetProperty("losses").GetInt32()).Should().Be((3, 2, 1));
        opponent.GetProperty("meetings").GetArrayLength().Should().Be(3);
        opponent.GetProperty("veto").GetProperty("vetoes").GetInt32().Should().Be(1);

        var stranger = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(stranger);
        (await factory.CreateAuthenticatedClient(stranger).GetAsync($"/api/scrims/teams/{w.A}/opponents")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
