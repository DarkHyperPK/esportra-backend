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

namespace Esportra.Api.Tests.ScrimData;

[Collection(IntegrationCollection.Name)]
public sealed class ScrimDataEndpointTests(ApiFactory factory)
{
    private sealed class World
    {
        public required string Prefix { get; init; }
        public required Guid[] Users { get; init; }   // 0-4 team A, 5-9 team O
        public required Guid A { get; init; }
        public required Guid O { get; init; }
        public Guid Member => Users[1];
    }

    private static readonly Round[] Rounds =
    [
        new(0, "Blue", "Attacker", [new Kill("p1", "p6", 5_000, 5_000)]),
        new(1, "Red", "Defender", [new Kill("p6", "p1", 4_000, 100_000)]),
    ];

    private async Task<World> SeedWorldAsync()
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
        return new World { Prefix = prefix, Users = users, A = a, O = o };
    }

    private string Payload(World w, string matchId, string mapCode, int blue, int red, int day) =>
        Regex.Replace(Build(matchId: matchId, mapId: $"/Game/Maps/{mapCode}/{mapCode}", blueRounds: blue, redRounds: red, rounds: Rounds,
                startMillis: new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).AddDays(day).ToUnixTimeMilliseconds(), agentOf: i => $"agent-{i}").GetRawText(),
            "\"p(\\d+)\"", m => $"\"{w.Prefix}p{m.Groups[1].Value}\"");

    private static string OcrData(World w, bool teamIsOne = true)
    {
        var mine = Enumerable.Range(0, 5).Select(i => new
        {
            gameName = $"Player{i + 1}",
            characterId = $"agent-{i + 1}",
            isTeam1 = teamIsOne,
            isTeam2 = !teamIsOne,
            userId = w.Users[i].ToString(),
            kills = 20,
            deaths = 10,
            assists = 5,
            acs = 250,
            adr = 150,
            hsPct = 25,
            firstBloods = 3,
            plants = 1,
            defuses = 0,
        });
        var theirs = Enumerable.Range(5, 5).Select(i => new { gameName = $"Player{i + 1}", characterId = $"agent-{i + 1}", isTeam1 = !teamIsOne, isTeam2 = teamIsOne, userId = (string?)null, kills = 9, deaths = 15, assists = 2, acs = 150, adr = 100, hsPct = 15, firstBloods = 0, plants = 0, defuses = 0 });
        return JsonSerializer.Serialize(new { source = "ocr", players = mine.Cast<object>().Concat(theirs) });
    }

    /// <summary>Inserts one scrim map in its own session and returns the game id.</summary>
    private async Task<Guid> MapAsync(World w, string status, string source, int s1, int s2, string? map, string? riotMatchId = null, string? matchData = null)
    {
        await using var conn = factory.Seeder.OpenConnection();
        var session = await conn.QuerySingleAsync<Guid>("INSERT INTO public.scrim_sessions (team1_id, team2_id, game_slug, status) VALUES (@a, @o, 'valorant', 'completed') RETURNING id", new { a = w.A, o = w.O });
        return await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.scrim_session_games (session_id, game_number, status, map_name, team1_score, team2_score, winner_slot, source, riot_match_id, match_data, reported_by_team_id)
            VALUES (@session, 1, @status, @map, @s1, @s2, @winner, @source, @riot, @data::jsonb, @a) RETURNING id
            """, new { session, status, map, s1, s2, winner = status is "confirmed" or "resolved" ? (s1 > s2 ? 1 : 2) : (int?)null, source, riot = riotMatchId, data = matchData, a = w.A });
    }

    private async Task RiotMapAsync(World w, string mapCode, string typed, int reported1, int reported2, int blue, int red, int day)
    {
        var matchId = Guid.NewGuid().ToString();
        var gameId = await MapAsync(w, "confirmed", "riot", reported1, reported2, typed, matchId);
        await using var conn = factory.Seeder.OpenConnection();
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, new ProviderMatchRegistration(ProviderMatchContext.ScrimGame, gameId, null, 1, matchId, w.A, w.O, reported1, reported2, typed));
        await ProviderMatchIngest.ApplyAsync(conn, reg!.AssociationId, Payload(w, matchId, mapCode, blue, red, day), "eu", true);
    }

    /// <summary>
    /// Team A's season: 2 auto-fetched Riot maps (Ascent, win then loss), 2 screenshot maps (Haven, both wins), a typed map (Bind, loss),
    /// a Riot map whose score does not match Riot (Split, still a confirmed win), plus one map awaiting confirmation and one disputed.
    /// </summary>
    private async Task<World> SeedSeasonAsync()
    {
        var w = await SeedWorldAsync();
        await RiotMapAsync(w, "Ascent", "Ascent", 13, 7, 13, 7, day: 1);
        await RiotMapAsync(w, "Ascent", "Ascent", 7, 13, 7, 13, day: 2);
        await MapAsync(w, "confirmed", "ocr", 13, 9, "Haven", matchData: OcrData(w));
        await MapAsync(w, "confirmed", "ocr", 13, 5, "haven", matchData: OcrData(w));
        await MapAsync(w, "confirmed", "manual", 5, 13, "Bind");
        await RiotMapAsync(w, "Bonsai", "Split", 13, 3, 13, 7, day: 6);   // reported 13-3, Riot says 13-7: the association is disputed
        await MapAsync(w, "reported", "manual", 13, 1, "Lotus");
        await MapAsync(w, "disputed", "manual", 13, 2, "Icebox");
        return w;
    }

    private async Task<JsonElement> GetAsync(Guid viewer, Guid team)
    {
        var response = await factory.CreateAuthenticatedClient(viewer).GetAsync($"/api/scrims/teams/{team}/data");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Coverage_says_how_much_of_the_data_is_full_limited_or_just_a_score()
    {
        var w = await SeedSeasonAsync();

        var json = await GetAsync(w.Member, w.A);

        var c = json.GetProperty("coverage");
        (c.GetProperty("maps").GetInt32(), c.GetProperty("full").GetInt32(), c.GetProperty("screenshot").GetInt32(), c.GetProperty("score_only").GetInt32())
            .Should().Be((6, 2, 2, 2));
        (c.GetProperty("riot_not_verified").GetInt32(), c.GetProperty("riot_pending").GetInt32()).Should().Be((1, 0));
        (c.GetProperty("awaiting_confirmation").GetInt32(), c.GetProperty("disputed").GetInt32()).Should().Be((1, 1));
        var record = json.GetProperty("record");
        (record.GetProperty("games").GetInt32(), record.GetProperty("wins").GetInt32(), record.GetProperty("losses").GetInt32()).Should().Be((6, 4, 2));
        json.GetProperty("form").EnumerateArray().Select(x => x.GetString()).Should().HaveCount(5);
        json.GetRawText().Should().NotContain("match_data").And.NotContain("puuid").And.NotContain(w.Prefix + "p");
    }

    [Fact]
    public async Task The_map_pool_combines_every_tier_but_attack_and_defense_come_from_full_data_only()
    {
        var w = await SeedSeasonAsync();

        var maps = (await GetAsync(w.Member, w.A)).GetProperty("maps").EnumerateArray().ToDictionary(m => m.GetProperty("map").GetString()!);

        maps.Keys.Should().BeEquivalentTo("Ascent", "Haven", "Bind", "Split");   // "haven" and "Haven" are one map
        (maps["Ascent"].GetProperty("games").GetInt32(), maps["Ascent"].GetProperty("wins").GetInt32()).Should().Be((2, 1));
        (maps["Haven"].GetProperty("games").GetInt32(), maps["Haven"].GetProperty("wins").GetInt32()).Should().Be((2, 2));
        maps["Ascent"].GetProperty("attack").GetProperty("played").GetInt32().Should().Be(4);
        maps["Haven"].GetProperty("attack").GetProperty("played").GetInt32().Should().Be(0);
        maps["Haven"].GetProperty("attack").GetProperty("pct").ValueKind.Should().Be(JsonValueKind.Null);
        maps.Values.Should().OnlyContain(m => m.GetProperty("signal").GetString() == "insufficient_sample");
    }

    [Fact]
    public async Task Compositions_come_from_riot_and_screenshots_alike()
    {
        var w = await SeedSeasonAsync();

        var comps = (await GetAsync(w.Member, w.A)).GetProperty("compositions").EnumerateArray().ToList();

        var main = comps.Should().ContainSingle().Subject;
        main.GetProperty("agents").EnumerateArray().Select(a => a.GetString()).Should().BeEquivalentTo("agent-1", "agent-2", "agent-3", "agent-4", "agent-5");
        (main.GetProperty("games").GetInt32(), main.GetProperty("wins").GetInt32()).Should().Be((4, 3));
    }

    [Fact]
    public async Task Each_player_figure_comes_only_from_the_maps_that_provide_it()
    {
        var w = await SeedSeasonAsync();

        var players = (await GetAsync(w.Member, w.A)).GetProperty("players").EnumerateArray().ToList();

        players.Should().HaveCount(5);
        var first = players.Single(p => p.GetProperty("user_id").GetGuid() == w.Users[0]);   // the one who gets the first kill and first death in the Riot rounds
        (first.GetProperty("games").GetInt32(), first.GetProperty("full_games").GetInt32(), first.GetProperty("screenshot_games").GetInt32()).Should().Be((4, 2, 2));
        first.GetProperty("kd").GetDouble().Should().Be(1.67);          // (10+10+20+20) / (8+8+10+10)
        first.GetProperty("acs").GetDouble().Should().Be(225.0);        // weighted by rounds: Riot 40 rounds at 200, screenshots 40 rounds at 250
        (first.GetProperty("first_kills").GetInt32(), first.GetProperty("first_deaths").GetInt32(), first.GetProperty("first_death_games").GetInt32()).Should().Be((8, 2, 2));
        var other = players.Single(p => p.GetProperty("user_id").GetGuid() == w.Users[1]);
        other.GetProperty("first_deaths").GetInt32().Should().Be(0);
        other.GetProperty("agents")[0].GetProperty("character_id").GetString().Should().Be("agent-2");
    }

    [Fact]
    public async Task Lineups_follow_who_played_whatever_the_source()
    {
        var w = await SeedSeasonAsync();

        var roster = (await GetAsync(w.Member, w.A)).GetProperty("roster");

        var lineup = roster.GetProperty("lineups").EnumerateArray().Should().ContainSingle().Subject;
        (lineup.GetProperty("games").GetInt32(), lineup.GetProperty("wins").GetInt32(), lineup.GetProperty("complete").GetBoolean()).Should().Be((4, 3, true));
        roster.GetProperty("stability").GetProperty("players_used").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Only_the_teams_own_members_and_admins_can_read_it()
    {
        var w = await SeedSeasonAsync();
        var admin = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(admin);
        await factory.Seeder.SeedAdminPanelRoleAsync(admin);
        var url = $"/api/scrims/teams/{w.A}/data";

        (await factory.CreateAuthenticatedClient(w.Users[7]).GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);   // the opponent
        (await factory.CreateClient().GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await factory.CreateAuthenticatedClient(admin).GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.CreateAuthenticatedClient(w.Users[4]).GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.CreateAuthenticatedClient(w.Member).GetAsync($"/api/scrims/teams/{Guid.NewGuid()}/data")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE public.team_members SET is_active = FALSE WHERE team_id = @a AND user_id = @u", new { a = w.A, u = w.Users[4] });
        (await factory.CreateAuthenticatedClient(w.Users[4]).GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);   // left the team
    }

    [Fact]
    public async Task A_team_with_no_confirmed_scrim_maps_gets_an_honest_empty_answer()
    {
        var w = await SeedWorldAsync();
        await MapAsync(w, "reported", "manual", 13, 7, "Ascent");

        var json = await GetAsync(w.Member, w.A);

        json.GetProperty("has_data").GetBoolean().Should().BeFalse();
        json.GetProperty("coverage").GetProperty("awaiting_confirmation").GetInt32().Should().Be(1);
        json.GetProperty("record").GetProperty("win_rate_pct").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("players").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task The_other_team_sees_its_own_side_of_the_same_scrims()
    {
        var w = await SeedSeasonAsync();

        var record = (await GetAsync(w.Users[5], w.O)).GetProperty("record");

        (record.GetProperty("games").GetInt32(), record.GetProperty("wins").GetInt32(), record.GetProperty("losses").GetInt32()).Should().Be((6, 2, 4));
    }
}
