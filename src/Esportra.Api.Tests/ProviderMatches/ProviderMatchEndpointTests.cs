using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.ScheduledJobs;
using Esportra.Api.Services.ProviderMatches;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static Esportra.Api.Tests.ProviderMatches.ValorantMatchFixture;

namespace Esportra.Api.Tests.ProviderMatches;

[Collection(IntegrationCollection.Name)]
public sealed class ProviderMatchEndpointTests(ApiFactory factory)
{
    private sealed class RecordingTrigger : IProviderMatchIngestTrigger
    {
        public List<Guid> Queued { get; } = [];
        public void Queue(Guid associationId) => Queued.Add(associationId);
    }

    /// <summary>A host with a recording trigger. Callers dispose it: an undisposed host keeps its Hangfire server and database connections for the rest of the run.</summary>
    private (WebApplicationFactory<Program> Host, RecordingTrigger Trigger) HostWithRecorder()
    {
        var trigger = new RecordingTrigger();
        var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IProviderMatchIngestTrigger>();
            s.AddSingleton<IProviderMatchIngestTrigger>(trigger);
        }));
        return (host, trigger);
    }

    private static HttpClient As(WebApplicationFactory<Program> host, Guid user)
    {
        var c = host.CreateClient();
        c.DefaultRequestHeaders.Add("Authorization", TestAuthHelper.BearerHeader(user));
        return c;
    }

    private sealed record Scrim(Guid Captain1, Guid Captain2, Guid Outsider, Guid Team1, Guid Team2, Guid SessionId, string Prefix);

    private async Task<Scrim> SeedScrimAsync()
    {
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var users = Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var u in users) await factory.Seeder.SeedAuthUserAsync(u);
        var team1 = await factory.Seeder.SeedTeamAsync(users[0], "Alpha " + prefix);
        var team2 = await factory.Seeder.SeedTeamAsync(users[5], "Bravo " + prefix);
        await using var conn = factory.Seeder.OpenConnection();
        for (var i = 0; i < 10; i++)
        {
            await conn.ExecuteAsync(
                "INSERT INTO public.team_members (team_id, user_id, role) VALUES (@team, @user, 'member') ON CONFLICT (team_id, user_id) DO NOTHING",
                new { team = i < 5 ? team1 : team2, user = users[i] });
            await conn.ExecuteAsync(
                "INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@user, @puuid, @name, 'EUW')",
                new { user = users[i], puuid = $"{prefix}p{i + 1}", name = $"Player{i + 1}" });
        }
        var session = await conn.QuerySingleAsync<Guid>(
            "INSERT INTO public.scrim_sessions (team1_id, team2_id, game_slug, status) VALUES (@team1, @team2, 'valorant', 'in_progress') RETURNING id",
            new { team1, team2 });
        return new Scrim(users[0], users[5], users[10], team1, team2, session, prefix);
    }

    private static string Payload(Scrim s, string matchId) =>
        System.Text.RegularExpressions.Regex.Replace(
            Build(matchId: matchId, rounds: [new Round(0, "Blue", "Attacker", [new Kill("p1", "p6", 5_000, 5_000)])]).GetRawText(),
            "\"p(\\d+)\"", m => $"\"{s.Prefix}p{m.Groups[1].Value}\"");

    [Fact]
    public async Task Reporting_a_riot_scrim_map_registers_the_match_and_never_fails_the_report()
    {
        var (host, trigger) = HostWithRecorder();
        using var disposeHost = host;
        var s = await SeedScrimAsync();
        var matchId = Guid.NewGuid().ToString();
        var client = As(host, s.Captain1);

        var response = await client.PostAsJsonAsync($"/api/scrims/sessions/{s.SessionId}/games/1/report", new
        {
            reported_by_team_id = s.Team1,
            team1_score = 13,
            team2_score = 7,
            source = "riot",
            riot_match_id = matchId,
            map_name = "Ascent",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using var conn = factory.Seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM public.provider_match_associations a JOIN public.provider_matches m ON m.id = a.match_id WHERE m.provider_match_id = @matchId AND a.context_type = 'scrim_game'",
            new { matchId })).Should().Be(1);
        trigger.Queued.Should().HaveCount(1);

        // A malformed id registers nothing, and the report still goes through.
        await using var other = factory.Seeder.OpenConnection();
        var session2 = await other.QuerySingleAsync<Guid>(
            "INSERT INTO public.scrim_sessions (team1_id, team2_id, game_slug, status) VALUES (@t1, @t2, 'valorant', 'in_progress') RETURNING id",
            new { t1 = s.Team1, t2 = s.Team2 });
        var bad = await client.PostAsJsonAsync($"/api/scrims/sessions/{session2}/games/1/report", new
        {
            reported_by_team_id = s.Team1,
            team1_score = 13,
            team2_score = 5,
            source = "manual",
            riot_match_id = "../../x",
        });
        bad.StatusCode.Should().Be(HttpStatusCode.OK, await bad.Content.ReadAsStringAsync());
        trigger.Queued.Should().HaveCount(1);
    }

    [Fact]
    public async Task Scrim_members_read_the_verified_scoreboard_and_outsiders_cannot()
    {
        var (host, _) = HostWithRecorder();
        using var disposeHost = host;
        var s = await SeedScrimAsync();
        var matchId = Guid.NewGuid().ToString();
        await using var conn = factory.Seeder.OpenConnection();
        var gameId = await conn.QuerySingleAsync<Guid>(
            "INSERT INTO public.scrim_session_games (session_id, game_number, team1_score, team2_score, source, riot_match_id, reported_by_team_id) VALUES (@sid, 1, 13, 7, 'riot', @matchId, @t) RETURNING id",
            new { sid = s.SessionId, matchId, t = s.Team1 });
        var reg = await ProviderMatchRegistry.RegisterAsync(conn, new ProviderMatchRegistration(
            ProviderMatchContext.ScrimGame, gameId, null, 1, matchId, s.Team1, s.Team2, 13, 7, "Ascent"));
        await ProviderMatchIngest.ApplyAsync(conn, reg!.AssociationId, Payload(s, matchId), "eu", true);
        var url = $"/api/scrims/sessions/{s.SessionId}/games/1/provider-match";

        var ok = await As(host, s.Captain2).GetAsync(url);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ok.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("status").GetString().Should().Be("verified");
        json.GetProperty("players").GetArrayLength().Should().Be(10);
        json.GetProperty("players")[0].GetProperty("is_roster_member").GetBoolean().Should().BeTrue();
        body.Should().NotContain("puuid").And.NotContain(s.Prefix);

        (await As(host, s.Outsider).GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await host.CreateClient().GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await As(host, s.Captain1).GetAsync($"/api/scrims/sessions/{s.SessionId}/games/9/provider-match")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_game_without_riot_data_reports_status_none()
    {
        var (host, _) = HostWithRecorder();
        using var disposeHost = host;
        var s = await SeedScrimAsync();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync(
            "INSERT INTO public.scrim_session_games (session_id, game_number, team1_score, team2_score, reported_by_team_id) VALUES (@sid, 1, 13, 7, @t)",
            new { sid = s.SessionId, t = s.Team1 });

        var json = await (await As(host, s.Captain1).GetAsync($"/api/scrims/sessions/{s.SessionId}/games/1/provider-match")).Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("status").GetString().Should().Be("none");

        var t = await (await As(host, s.Outsider).GetAsync($"/api/matches/{Guid.NewGuid()}/games/1/provider-match")).Content.ReadFromJsonAsync<JsonElement>();
        t.GetProperty("status").GetString().Should().Be("none");
        (await As(host, s.Outsider).GetAsync($"/api/matches/{Guid.NewGuid()}/games/99/provider-match")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Analytics_visibility_defaults_to_private_and_is_per_player()
    {
        var (host, _) = HostWithRecorder();
        using var disposeHost = host;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(a);
        await factory.Seeder.SeedAuthUserAsync(b);

        (await As(host, a).GetFromJsonAsync<JsonElement>("/api/competitive/analytics-visibility")).GetProperty("visibility").GetString().Should().Be("private");
        (await As(host, a).PutAsJsonAsync("/api/competitive/analytics-visibility", new { game = "valorant", visibility = "public" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await As(host, a).GetFromJsonAsync<JsonElement>("/api/competitive/analytics-visibility?game=valorant")).GetProperty("visibility").GetString().Should().Be("public");
        (await As(host, b).GetFromJsonAsync<JsonElement>("/api/competitive/analytics-visibility")).GetProperty("visibility").GetString().Should().Be("private");

        await using var conn = factory.Seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<bool>("SELECT consented_at IS NOT NULL FROM public.player_analytics_settings WHERE user_id = @a", new { a })).Should().BeTrue();
        (await As(host, a).PutAsJsonAsync("/api/competitive/analytics-visibility", new { visibility = "private" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await conn.ExecuteScalarAsync<bool>("SELECT consented_at IS NULL FROM public.player_analytics_settings WHERE user_id = @a", new { a })).Should().BeTrue();
        (await As(host, a).PutAsJsonAsync("/api/competitive/analytics-visibility", new { visibility = "everyone" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await As(host, a).PutAsJsonAsync("/api/competitive/analytics-visibility", new { game = "Bad Game!", visibility = "public" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Manual_association_and_backfill_are_staff_only_and_audited()
    {
        var (host, trigger) = HostWithRecorder();
        using var disposeHost = host;
        var s = await SeedScrimAsync();
        var admin = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(admin);
        await factory.Seeder.SeedAdminPanelRoleAsync(admin);
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync(
            "INSERT INTO public.scrim_session_games (session_id, game_number, team1_score, team2_score, reported_by_team_id) VALUES (@sid, 1, 13, 7, @t)",
            new { sid = s.SessionId, t = s.Team1 });
        var matchId = Guid.NewGuid().ToString();
        var body = new { context_type = "scrim_game", scrim_session_id = s.SessionId, game_number = 1, riot_match_id = matchId };

        (await As(host, s.Captain1).PostAsJsonAsync("/api/admin/provider-matches/associations", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await As(host, s.Captain1).PostAsync("/api/admin/provider-matches/backfill", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var ok = await As(host, admin).PostAsJsonAsync("/api/admin/provider-matches/associations", body);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        trigger.Queued.Should().HaveCount(1);
        var ev = await conn.QuerySingleAsync<(string Type, Guid? Actor)>(
            "SELECT event_type, actor_user_id FROM public.provider_match_association_events WHERE association_id = @id ORDER BY created_at LIMIT 1", new { id = trigger.Queued[0] });
        ev.Should().Be(("manual_association", admin));

        (await As(host, admin).PostAsJsonAsync("/api/admin/provider-matches/associations", new { context_type = "scrim_game", scrim_session_id = s.SessionId, game_number = 1, riot_match_id = "../bad" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await As(host, admin).PostAsync("/api/admin/provider-matches/backfill?limit=5", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
