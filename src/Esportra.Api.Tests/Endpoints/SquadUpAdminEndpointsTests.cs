using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

[Collection(IntegrationCollection.Name)]
public sealed class SquadUpAdminEndpointsTests(ApiFactory factory) : IAsyncLifetime
{
    private const string Swipe = "/api/competitive/lfg/swipe";
    private const string Admin = "/api/admin/squad-up";

    // One database for the whole suite: a private region per test keeps live-pool counts and samples exact.
    private readonly string region = $"t{Guid.NewGuid():N}"[..10];

    public async Task InitializeAsync() => await factory.Seeder.SeedFeatureFlagAsync("squad_up", enabled: true);
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Player(Guid Id, HttpClient Client);

    private async Task<Player> Queued(int tier = 15)
    {
        var id = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(id);
        await using (var countryConn = factory.Seeder.OpenConnection())
            await countryConn.ExecuteAsync("UPDATE public.profiles SET country_code = 'PK' WHERE id = @id", new { id });
        await using (var conn = factory.Seeder.OpenConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line, region) VALUES (@id, @puuid, 'P', 'T', @region);
                INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, competitive_tier, competitive_tier_name, sync_status, synced_at)
                VALUES (@id, 'valorant', @puuid, @tier, 'Gold 1', 'complete', now());
                """, new { id, puuid = $"puuid-{id}", region, tier });
        }
        var player = new Player(id, factory.CreateAuthenticatedClient(id));
        (await player.Client.PostAsJsonAsync($"{Swipe}/session", new { mic_on = true, slots_needed = 3, area = "south-asia" })).EnsureSuccessStatusCode();
        return player;
    }

    private async Task<HttpClient> AdminWith(params string[] permissions)
    {
        var id = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(id);
        if (permissions.Length == 0) return factory.CreateAuthenticatedClient(id);
        await using var conn = factory.Seeder.OpenConnection();
        var key = $"squad-test-{id:N}";
        await conn.ExecuteAsync(
            """
            WITH role AS (INSERT INTO public.admin_roles (name, key, description) VALUES (@key, @key, 'test') RETURNING id)
            INSERT INTO public.admin_user_roles (user_id, role_id) SELECT @id, id FROM role;
            INSERT INTO public.admin_role_permissions (role_id, permission_id)
            SELECT r.id, p.id FROM public.admin_roles r, public.admin_permissions p WHERE r.key = @key AND p.name = ANY(@permissions);
            """, new { id, key, permissions });
        return factory.CreateAuthenticatedClient(id);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Admin_Routes_RejectPlayersWithoutPermission()
    {
        var player = await AdminWith();
        (await player.GetAsync($"{Admin}/overview")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await player.GetAsync($"{Admin}/sessions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await player.GetAsync($"{Admin}/matches")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await player.PostAsync($"{Admin}/sessions/{Guid.NewGuid()}/end", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await factory.CreateClient().GetAsync($"{Admin}/overview")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ViewPermission_ReadsButCannotEnd_ManagePermissionCan()
    {
        var target = await Queued();
        var viewer = await AdminWith("squad_up:view");
        var manager = await AdminWith("squad_up:view", "squad_up:manage");

        (await viewer.GetAsync($"{Admin}/overview")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsync($"{Admin}/sessions/{target.Id}/end", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.PostAsync($"{Admin}/sessions/{target.Id}/end", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await manager.PostAsync($"{Admin}/sessions/{target.Id}/end", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await target.Client.PostAsync($"{Swipe}/session/heartbeat", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Overview_CountsLivePlayersAndRecordsSessionsSwipesAndMatchesDurably()
    {
        var admin = await AdminWith("squad_up:view");
        var before = (await Json(await admin.GetAsync($"{Admin}/overview"))).GetProperty("totals");
        var a = await Queued();
        var b = await Queued();
        var c = await Queued();
        await a.Client.PostAsync($"{Swipe}/{b.Id}/right", null);
        await a.Client.PostAsync($"{Swipe}/{b.Id}/right", null); // repeat must not double count
        await a.Client.PostAsync($"{Swipe}/{c.Id}/left", null);
        await b.Client.PostAsync($"{Swipe}/{a.Id}/right", null); // mutual -> match

        var after = await Json(await admin.GetAsync($"{Admin}/overview"));
        var totals = after.GetProperty("totals");
        int Delta(string name) => totals.GetProperty(name).GetInt32() - before.GetProperty(name).GetInt32();
        Delta("sessionsStarted").Should().Be(3);
        Delta("swipesRight").Should().Be(2);
        Delta("swipesLeft").Should().Be(1);
        Delta("matches").Should().Be(1);
        var kr = after.GetProperty("live").GetProperty("byRegion").EnumerateArray().Single(r => r.GetProperty("region").GetString() == region);
        kr.GetProperty("players").GetInt32().Should().Be(3);
        after.GetProperty("flag").GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RestartingALiveSession_IsNotCountedAsANewSession()
    {
        var admin = await AdminWith("squad_up:view");
        var p = await Queued();
        var before = (await Json(await admin.GetAsync($"{Admin}/overview"))).GetProperty("totals").GetProperty("sessionsStarted").GetInt32();
        (await p.Client.PostAsJsonAsync($"{Swipe}/session", new { mic_on = false, slots_needed = 2, area = "south-asia" })).EnsureSuccessStatusCode();
        var after = (await Json(await admin.GetAsync($"{Admin}/overview"))).GetProperty("totals").GetProperty("sessionsStarted").GetInt32();
        after.Should().Be(before);
    }

    [Fact]
    public async Task Sessions_And_Matches_ListWhatAdminsNeedToModerate()
    {
        var admin = await AdminWith("squad_up:view");
        var a = await Queued();
        var b = await Queued();
        await a.Client.PostAsync($"{Swipe}/{b.Id}/right", null);
        await b.Client.PostAsync($"{Swipe}/{a.Id}/right", null);

        var sessions = (await Json(await admin.GetAsync($"{Admin}/sessions?limit=200"))).EnumerateArray().ToList();
        var row = sessions.Single(s => s.GetProperty("userId").GetGuid() == a.Id);
        row.GetProperty("likesSent").GetInt32().Should().Be(1);
        row.GetProperty("region").GetString().Should().Be(region);

        var matches = (await Json(await admin.GetAsync($"{Admin}/matches?limit=200"))).EnumerateArray().ToList();
        matches.Should().Contain(m => new[] { m.GetProperty("user1Id").GetGuid(), m.GetProperty("user2Id").GetGuid() }.OrderBy(x => x).SequenceEqual(new[] { a.Id, b.Id }.OrderBy(x => x)));
    }

    [Fact]
    public async Task FeatureFlag_WasRenamedToSquadUp_AndAdminPermissionsExist()
    {
        await using var conn = factory.Seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.feature_flags WHERE key = 'lfg_queue'")).Should().Be(0);
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.feature_flags WHERE key = 'squad_up'")).Should().Be(1);
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.admin_permissions WHERE name IN ('squad_up:view', 'squad_up:manage')")).Should().Be(2);
    }
}
