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

namespace Esportra.Api.Tests.Career;

[Collection(IntegrationCollection.Name)]
public sealed class CareerEndpointTests(ApiFactory factory)
{
    /// <summary>
    /// Team A (users 0-4) and opponents O (users 5-9), all with linked Riot accounts. The player is users[1] (p2).
    /// Two scrims are played for A; the player then leaves A and joins team C, whose captain is a new, unlinked user.
    /// </summary>
    private sealed class World
    {
        public required string Prefix { get; init; }
        public required Guid[] Users { get; init; }
        public required Guid TeamA { get; init; }
        public required Guid TeamO { get; init; }
        public Guid TeamC { get; set; }
        public Guid CaptainC { get; set; }
        public Guid Session { get; init; }
        public Guid Player => Users[1];
        public Guid CaptainA => Users[0];
    }

    private static readonly Round[] Rounds =
    [
        new(0, "Blue", "Attacker", [new Kill("p2", "p6", 5_000, 5_000), new Kill("p2", "p7", 9_000, 9_000)],
            Damage: [new Dmg("p2", "p6", 150, 1, 1, 0)]),
        new(1, "Red", "Defender", [new Kill("p6", "p2", 4_000, 100_000)]),
    ];

    private static long Day(int n) => new DateTimeOffset(2026, 10, n, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private string Payload(World w, string matchId, int blue, int red, int day) =>
        Regex.Replace(Build(matchId: matchId, blueRounds: blue, redRounds: red, rounds: Rounds, startMillis: Day(day)).GetRawText(),
            "\"p(\\d+)\"", m => $"\"{w.Prefix}p{m.Groups[1].Value}\"");

    private async Task<World> SeedWorldAsync()
    {
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var users = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var u in users) await factory.Seeder.SeedAuthUserAsync(u);
        var teamA = await factory.Seeder.SeedTeamAsync(users[0], "Alpha " + prefix);
        var teamO = await factory.Seeder.SeedTeamAsync(users[5], "Omega " + prefix);
        await using var conn = factory.Seeder.OpenConnection();
        for (var i = 0; i < 10; i++)
        {
            await conn.ExecuteAsync("INSERT INTO public.team_members (team_id, user_id, role) VALUES (@t, @u, 'member') ON CONFLICT (team_id, user_id) DO NOTHING",
                new { t = i < 5 ? teamA : teamO, u = users[i] });
            await conn.ExecuteAsync("INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@u, @p, @n, 'EUW')",
                new { u = users[i], p = $"{prefix}p{i + 1}", n = $"Player{i + 1}" });
        }
        var session = await conn.QuerySingleAsync<Guid>(
            "INSERT INTO public.scrim_sessions (team1_id, team2_id, game_slug, status) VALUES (@a, @o, 'valorant', 'completed') RETURNING id", new { a = teamA, o = teamO });
        return new World { Prefix = prefix, Users = users, TeamA = teamA, TeamO = teamO, Session = session };
    }

    /// <summary>Reports and ingests one scrim map between A and O.</summary>
    private async Task PlayScrimAsync(World w, int number, int blue, int red, int day)
    {
        var matchId = Guid.NewGuid().ToString();
        await using var conn = factory.Seeder.OpenConnection();
        var gameId = await conn.QuerySingleAsync<Guid>(
            "INSERT INTO public.scrim_session_games (session_id, game_number, team1_score, team2_score, source, riot_match_id, reported_by_team_id) VALUES (@s, @n, @b, @r, 'riot', @m, @t) RETURNING id",
            new { s = w.Session, n = number, b = blue, r = red, m = matchId, t = w.TeamA });
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, new ProviderMatchRegistration(
            ProviderMatchContext.ScrimGame, gameId, null, number, matchId, w.TeamA, w.TeamO, blue, red, "Ascent"));
        await ProviderMatchIngest.ApplyAsync(conn, reg!.AssociationId, Payload(w, matchId, blue, red, day), "eu", true);
    }

    /// <summary>Reports and ingests one tournament game between C (team 1) and O.</summary>
    private async Task<Guid> PlayTournamentAsync(World w, int gameNumber, int blue, int red, int day, int? reportedRed = null, string? matchId = null)
    {
        matchId ??= Guid.NewGuid().ToString();
        var tournamentMatch = Guid.NewGuid();
        await using var conn = factory.Seeder.OpenConnection();
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, new ProviderMatchRegistration(
            ProviderMatchContext.TournamentGame, ProviderMatchRegistry.TournamentGameContextId(tournamentMatch, gameNumber), tournamentMatch,
            gameNumber, matchId, w.TeamC, w.TeamO, blue, reportedRed ?? red, "Ascent"));
        if (reg!.NeedsIngest)
            await ProviderMatchIngest.ApplyAsync(conn, reg.AssociationId, Payload(w, matchId, blue, red, day), "eu", true);
        return tournamentMatch;
    }

    private async Task MoveToTeamCAsync(World w)
    {
        w.CaptainC = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(w.CaptainC);
        w.TeamC = await factory.Seeder.SeedTeamAsync(w.CaptainC, "Charlie " + w.Prefix);
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE public.team_members SET is_active = FALSE WHERE team_id = @a AND user_id = @u", new { a = w.TeamA, u = w.Player });
        foreach (var i in new[] { 1, 2, 3, 4 })
            await conn.ExecuteAsync("INSERT INTO public.team_members (team_id, user_id, role) VALUES (@c, @u, 'member') ON CONFLICT (team_id, user_id) DO NOTHING", new { c = w.TeamC, u = w.Users[i] });
    }

    /// <summary>The standard career: two scrims for A (win, loss), then one tournament game for C (win).</summary>
    private async Task<World> SeedCareerAsync()
    {
        var w = await SeedWorldAsync();
        await PlayScrimAsync(w, 1, 13, 7, day: 1);
        await PlayScrimAsync(w, 2, 5, 13, day: 2);
        await MoveToTeamCAsync(w);
        await PlayTournamentAsync(w, 1, 13, 9, day: 3);
        return w;
    }

    private async Task<JsonElement> GetAsync(Guid viewer, string path)
    {
        var response = await factory.CreateAuthenticatedClient(viewer).GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Career(Guid player) => $"/api/competitive/players/{player}/career";

    [Fact]
    public async Task The_player_sees_a_career_that_follows_them_across_teams()
    {
        var w = await SeedCareerAsync();

        var json = await GetAsync(w.Player, Career(w.Player));

        json.GetProperty("access").GetString().Should().Be("self");
        var t = json.GetProperty("totals");
        (t.GetProperty("matches").GetInt32(), t.GetProperty("scrim_matches").GetInt32(), t.GetProperty("tournament_matches").GetInt32())
            .Should().Be((3, 2, 1));
        (t.GetProperty("wins").GetInt32(), t.GetProperty("losses").GetInt32()).Should().Be((2, 1));
        (t.GetProperty("rounds").GetInt32(), t.GetProperty("acs").GetDouble(), t.GetProperty("kd").GetDouble(), t.GetProperty("adr").GetDouble())
            .Should().Be((60, 200.0, 1.25, 7.5));
        (t.GetProperty("first_kills").GetInt32(), t.GetProperty("first_deaths").GetInt32(), t.GetProperty("multi_kills").GetProperty("k2").GetInt32())
            .Should().Be((3, 3, 3));
        t.GetProperty("headshot_share_pct").GetDouble().Should().Be(50.0);

        var teams = json.GetProperty("teams").EnumerateArray().Select(x => (x.GetProperty("team_name").GetString()!, x.GetProperty("matches").GetInt32())).ToList();
        teams.Should().Equal(("Charlie " + w.Prefix, 1), ("Alpha " + w.Prefix, 2));

        var games = json.GetProperty("matches").EnumerateArray().ToList();
        games.Select(g => g.GetProperty("context").GetString()).Should().Equal("tournament", "scrim", "scrim");
        games[1].GetProperty("scrim_session_id").GetGuid().Should().Be(w.Session);
        games[0].GetProperty("map").GetString().Should().Be("Ascent");
        json.GetProperty("sample").GetProperty("matches").GetInt32().Should().Be(3);
        json.GetProperty("has_more").GetBoolean().Should().BeFalse();
        json.GetRawText().Should().NotContain(w.Prefix + "p", "PUUIDs never leave the server");
    }

    [Fact]
    public async Task Only_the_current_teams_staff_see_the_games_played_for_that_team()
    {
        var w = await SeedCareerAsync();

        var asCaptainC = await GetAsync(w.CaptainC, Career(w.Player));
        asCaptainC.GetProperty("access").GetString().Should().Be("team_staff");
        asCaptainC.GetProperty("scope").GetString().Should().Be("your_teams");
        asCaptainC.GetProperty("totals").GetProperty("matches").GetInt32().Should().Be(1);
        asCaptainC.GetProperty("matches").GetArrayLength().Should().Be(1);

        // The old team's captain no longer has a claim: the player left that team.
        (await GetAsync(w.CaptainA, Career(w.Player))).GetProperty("access").GetString().Should().Be("none");

        var stranger = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(stranger);
        var none = await GetAsync(stranger, Career(w.Player));
        none.GetProperty("access").GetString().Should().Be("none");
        none.TryGetProperty("totals", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_player_who_opts_in_shares_the_summary_but_not_their_scrim_games()
    {
        var w = await SeedCareerAsync();
        var stranger = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(stranger);

        (await factory.CreateAuthenticatedClient(w.Player).PutAsJsonAsync("/api/competitive/analytics-visibility", new { game = "valorant", visibility = "public" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await GetAsync(stranger, Career(w.Player));
        json.GetProperty("access").GetString().Should().Be("public");
        json.GetProperty("totals").GetProperty("matches").GetInt32().Should().Be(3);
        var games = json.GetProperty("matches").EnumerateArray().ToList();
        games.Should().ContainSingle().Which.GetProperty("context").GetString().Should().Be("tournament");
        json.GetProperty("has_more").GetBoolean().Should().BeFalse();

        // Team C's captain also sees the player's games for C (already tournament only here); the old captain sees only the public list.
        (await GetAsync(w.CaptainA, Career(w.Player))).GetProperty("matches").GetArrayLength().Should().Be(1);

        await factory.CreateAuthenticatedClient(w.Player).PutAsJsonAsync("/api/competitive/analytics-visibility", new { game = "valorant", visibility = "private" });
        (await GetAsync(stranger, Career(w.Player))).GetProperty("access").GetString().Should().Be("none");
    }

    [Fact]
    public async Task Staff_admins_see_the_whole_career()
    {
        var w = await SeedCareerAsync();
        var admin = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(admin);
        await factory.Seeder.SeedAdminPanelRoleAsync(admin);

        var json = await GetAsync(admin, Career(w.Player));

        json.GetProperty("access").GetString().Should().Be("admin");
        json.GetProperty("totals").GetProperty("matches").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Disputed_and_reused_riot_matches_are_not_counted()
    {
        var w = await SeedCareerAsync();
        var duplicateOf = Guid.NewGuid().ToString();
        await PlayTournamentAsync(w, 2, 13, 9, day: 4, matchId: duplicateOf);
        await PlayTournamentAsync(w, 3, 13, 9, day: 5, reportedRed: 2);            // reported 13-2, Riot says 13-9: disputed
        await PlayTournamentAsync(w, 4, 13, 9, day: 6, matchId: duplicateOf);      // same Riot match as game 2: duplicate

        var t = (await GetAsync(w.Player, Career(w.Player))).GetProperty("totals");

        t.GetProperty("matches").GetInt32().Should().Be(4);   // 3 original + game 2 only
    }

    [Fact]
    public async Task Game_history_pages_newest_first_and_says_exactly_when_it_ends()
    {
        var w = await SeedCareerAsync();
        var path = Career(w.Player) + "/matches";

        var first = await GetAsync(w.Player, path + "?limit=2");
        first.GetProperty("matches").EnumerateArray().Select(g => g.GetProperty("context").GetString()).Should().Equal("tournament", "scrim");
        first.GetProperty("has_more").GetBoolean().Should().BeTrue();

        var cursor = Uri.EscapeDataString(first.GetProperty("next_before").GetString()!);
        var second = await GetAsync(w.Player, path + $"?limit=2&before={cursor}");
        second.GetProperty("matches").GetArrayLength().Should().Be(1);
        second.GetProperty("has_more").GetBoolean().Should().BeFalse();
        second.GetProperty("next_before").ValueKind.Should().Be(JsonValueKind.Null);

        (await factory.CreateAuthenticatedClient(w.Player).GetAsync(Career(w.Player) + "?game=cs2")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ingesting_again_does_not_change_the_career()
    {
        var w = await SeedCareerAsync();
        var before = (await GetAsync(w.Player, Career(w.Player))).GetProperty("totals").GetRawText();
        await using var conn = factory.Seeder.OpenConnection();
        var ids = (await conn.QueryAsync<(Guid Id, string MatchId)>(
            "SELECT a.id, m.provider_match_id FROM public.provider_match_associations a JOIN public.provider_matches m ON m.id = a.match_id WHERE a.team1_id = @a OR a.team1_id = @c OR a.team2_id = @a", new { a = w.TeamA, c = w.TeamC })).ToList();
        ids.Should().HaveCount(3);

        foreach (var (id, matchId) in ids)
        {
            var payload = await conn.QuerySingleAsync<string>("SELECT payload::text FROM public.provider_match_payloads WHERE match_id = (SELECT id FROM public.provider_matches WHERE provider_match_id = @matchId)", new { matchId });
            await ProviderMatchIngest.ApplyAsync(conn, id, payload, "eu", false);
        }

        (await GetAsync(w.Player, Career(w.Player))).GetProperty("totals").GetRawText().Should().Be(before);
    }

    [Fact]
    public async Task A_player_with_no_verified_games_has_an_empty_record_with_no_invented_numbers()
    {
        var w = await SeedWorldAsync();

        var json = await GetAsync(w.Player, Career(w.Player));

        json.GetProperty("has_data").GetBoolean().Should().BeFalse();
        var t = json.GetProperty("totals");
        t.GetProperty("matches").GetInt32().Should().Be(0);
        t.GetProperty("kd").ValueKind.Should().Be(JsonValueKind.Null);
        t.GetProperty("win_rate_pct").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("matches").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Team_record_shows_members_everything_and_others_only_what_the_team_makes_public()
    {
        var w = await SeedCareerAsync();
        var stranger = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(stranger);

        var member = await GetAsync(w.CaptainA, $"/api/competitive/teams/{w.TeamA}/career");
        member.GetProperty("access").GetString().Should().Be("member");
        member.GetProperty("totals").GetProperty("matches").GetInt32().Should().Be(2);
        member.GetProperty("totals").GetProperty("round_win_rate_pct").GetDouble().Should().Be(Math.Round(100.0 * 18 / 38, 1));
        member.GetProperty("form").EnumerateArray().Select(x => x.GetString()).Should().Equal("L", "W");
        member.GetProperty("players").GetArrayLength().Should().Be(5);
        member.GetProperty("players")[0].GetProperty("matches").GetInt32().Should().Be(2);

        var publicView = await GetAsync(stranger, $"/api/competitive/teams/{w.TeamA}/career");
        publicView.GetProperty("access").GetString().Should().Be("public");
        publicView.GetProperty("includes_scrims").GetBoolean().Should().BeTrue();
        publicView.GetProperty("players").GetArrayLength().Should().Be(0);

        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE public.teams SET scrim_history_public = FALSE WHERE id = @a", new { a = w.TeamA });
        var hidden = await GetAsync(stranger, $"/api/competitive/teams/{w.TeamA}/career");
        hidden.GetProperty("includes_scrims").GetBoolean().Should().BeFalse();
        hidden.GetProperty("has_data").GetBoolean().Should().BeFalse();
        hidden.GetProperty("matches").GetArrayLength().Should().Be(0);

        var tournamentTeam = await GetAsync(stranger, $"/api/competitive/teams/{w.TeamC}/career");
        tournamentTeam.GetProperty("totals").GetProperty("tournament_matches").GetInt32().Should().Be(1);

        (await factory.CreateAuthenticatedClient(stranger).GetAsync($"/api/competitive/teams/{Guid.NewGuid()}/career")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await factory.CreateClient().GetAsync($"/api/competitive/teams/{w.TeamA}/career")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
