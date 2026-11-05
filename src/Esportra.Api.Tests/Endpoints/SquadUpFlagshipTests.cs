using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

/// <summary>Squad Up filters (mode, home area, rank range, shard), card notes, idle removal, Lobby mode and public stats.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class SquadUpFlagshipTests(ApiFactory factory) : IAsyncLifetime
{
    private const string Swipe = "/api/competitive/lfg/swipe";
    private const string Lobbies = "/api/competitive/lfg/swipe/lobbies";

    // One shared database: a private shard name per test keeps every pool to this test's players.
    private readonly string shard = $"t{Guid.NewGuid():N}"[..10];

    public async Task InitializeAsync() => await factory.Seeder.SeedFeatureFlagAsync("squad_up", enabled: true);
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Player(Guid Id, HttpClient Client);

    private async Task<Player> NewPlayer(string country = "PK", int? tier = 15, string? region = null, bool stats = true)
    {
        var id = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(id);
        await Exec(
            """
            UPDATE public.profiles SET country_code = @country WHERE id = @id;
            INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line, region) VALUES (@id, @puuid, 'P', 'T', @region);
            """, new { id, country, puuid = $"puuid-{id}", region = region ?? shard });
        if (stats)
            await Exec(
                """
                INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, competitive_tier, competitive_tier_name, sync_status, synced_at)
                VALUES (@id, 'valorant', @puuid, @tier, 'Gold 1', 'complete', now())
                """, new { id, puuid = $"puuid-{id}", tier });
        return new Player(id, factory.CreateAuthenticatedClient(id));
    }

    private static Task<HttpResponseMessage> Start(Player p, string mode = "competitive", string? area = "south-asia", int? minTier = null, int? maxTier = null,
        string? note = null) =>
        p.Client.PostAsJsonAsync($"{Swipe}/session", new { mic_on = true, slots_needed = 3, mode, area, min_tier = minTier, max_tier = maxTier, note });

    private async Task<Player> Queued(string country = "PK", int? tier = 15, string mode = "competitive", string area = "south-asia",
        int? minTier = null, int? maxTier = null, string? region = null, bool stats = true)
    {
        var p = await NewPlayer(country, tier, region, stats);
        (await Start(p, mode, area, minTier, maxTier)).StatusCode.Should().Be(HttpStatusCode.OK);
        return p;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task<string?> Error(HttpResponseMessage r) => (await Json(r)).GetProperty("error").GetString();

    private static async Task<List<Guid>> CardIds(Player p) =>
        (await Json(await p.Client.GetAsync($"{Swipe}/cards?limit=10"))).EnumerateArray().Select(c => c.GetProperty("userId").GetGuid()).ToList();

    private async Task Exec(string sql, object args)
    {
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync(sql, args);
    }

    private async Task<T> Scalar<T>(string sql, object args)
    {
        await using var conn = factory.Seeder.OpenConnection();
        return await conn.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("null scalar");
    }

    // ── Duo filters ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Players_OnlyMeetPlayersFromTheAreaTheyLiveIn()
    {
        var southAsia = await Queued("PK");
        var gulf = await Queued("AE", area: "middle-east");
        (await CardIds(southAsia)).Should().NotContain(gulf.Id);
        (await CardIds(gulf)).Should().NotContain(southAsia.Id);

        // Someone whose profile says PK but who lives in the Gulf picks Middle East and meets Gulf players.
        (await Start(southAsia, area: "middle-east")).EnsureSuccessStatusCode();
        (await CardIds(southAsia)).Should().Contain(gulf.Id);
        (await CardIds(gulf)).Should().Contain(southAsia.Id);
    }

    [Fact]
    public async Task Cards_ShowTheNoteAndPlayerCardArt()
    {
        var viewer = await Queued();
        var shown = await NewPlayer();
        await Exec("UPDATE public.player_game_stats SET player_card_id = 'card-1' WHERE user_id = @id", new { id = shown.Id });
        (await Start(shown, note: "  I smoke,\n you entry.  ")).EnsureSuccessStatusCode();
        var card = (await Json(await viewer.Client.GetAsync($"{Swipe}/cards?limit=10"))).EnumerateArray()
            .Single(c => c.GetProperty("userId").GetGuid() == shown.Id);
        card.GetProperty("note").GetString().Should().Be("I smoke, you entry.", "control characters and extra spaces are tidied");
        card.GetProperty("playerCardId").GetString().Should().Be("card-1");
        card.GetProperty("area").GetString().Should().Be("south-asia");
    }

    [Fact]
    public async Task Start_WithoutProfileCountry_StillWorks_TheAreaIsWhatMatters()
    {
        var noCountry = await NewPlayer();
        await Exec("UPDATE public.profiles SET country_code = NULL WHERE id = @id", new { id = noCountry.Id });
        (await Start(noCountry)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Modes_NeverMix_AndUnratedAcceptsUnrankedPlayers()
    {
        var competitive = await Queued();
        var unrated = await Queued(mode: "unrated");
        var unranked = await NewPlayer(tier: null, stats: true);
        (await Error(await Start(unranked))).Should().Be("NOT_RANKED");
        (await Start(unranked, "unrated")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await CardIds(competitive)).Should().NotContain([unrated.Id, unranked.Id]);
        var unratedCards = await Json(await unrated.Client.GetAsync($"{Swipe}/cards?limit=10"));
        var card = unratedCards.EnumerateArray().Single(c => c.GetProperty("userId").GetGuid() == unranked.Id);
        card.GetProperty("mode").GetString().Should().Be("unrated");
        card.GetProperty("competitiveTier").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task RankRange_IsCheckedBothWays()
    {
        var picky = await Queued(tier: 15, minTier: 18, maxTier: 22);
        var tooHighForThem = await Queued(tier: 20);   // default band 16–24 excludes 15
        var fits = await Queued(tier: 19);             // default band 15–23 includes 15, and 19 is in 18–22
        var cards = await CardIds(picky);
        cards.Should().Contain(fits.Id).And.NotContain(tooHighForThem.Id);
    }

    [Fact]
    public async Task DifferentShards_NeverSeeEachOther()
    {
        var here = await Queued();
        var elsewhere = await Queued(region: $"o{Guid.NewGuid():N}"[..10]);
        (await CardIds(here)).Should().NotContain(elsewhere.Id);
    }

    [Fact]
    public async Task Start_ValidatesAreaGameNoteAndFilters()
    {
        var p = await NewPlayer();
        (await Error(await Start(p, area: null))).Should().Be("INVALID_AREA");
        (await Error(await Start(p, area: "mars"))).Should().Be("INVALID_AREA");
        (await Error(await Start(p, minTier: 20, maxTier: 10))).Should().Be("INVALID_RANK_RANGE");
        (await Error(await Start(p, note: new string('a', 101)))).Should().Be("NOTE_TOO_LONG");
        (await Error(await p.Client.PostAsJsonAsync($"{Swipe}/session", new { mic_on = true, slots_needed = 1, game = "chess", area = "europe" }))).Should().Be("UNSUPPORTED_GAME");
        var ok = await Json(await Start(p, area: "South-Asia", note: "   "));
        ok.GetProperty("area").GetString().Should().Be("south-asia");
        ok.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null, "a blank note is no note");
    }

    // ── presence ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task IdlePlayers_DropOutAfterTenMinutes_EvenWithHeartbeats_UnlessTheySayStillHere()
    {
        var viewer = await Queued();
        var idle = await Queued();
        var stillHere = await Queued();
        await Exec("UPDATE public.lfg_swipe_sessions SET last_activity_at = now() - interval '9 minutes' WHERE user_id = ANY(@ids)",
            new { ids = new[] { idle.Id, stillHere.Id } });
        (await stillHere.Client.PostAsync($"{Swipe}/session/active", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await Exec("UPDATE public.lfg_swipe_sessions SET last_activity_at = now() - interval '11 minutes' WHERE user_id = @id", new { id = idle.Id });

        (await idle.Client.PostAsync($"{Swipe}/session/heartbeat", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var cards = await CardIds(viewer);
        cards.Should().Contain(stillHere.Id).And.NotContain(idle.Id);
    }

    [Fact]
    public async Task Swiping_CountsAsActivity()
    {
        var a = await Queued();
        var b = await Queued();
        await Exec("UPDATE public.lfg_swipe_sessions SET last_activity_at = now() - interval '9 minutes' WHERE user_id = @id", new { id = a.Id });
        (await a.Client.PostAsync($"{Swipe}/{b.Id}/left", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Scalar<bool>("SELECT last_activity_at > now() - interval '1 minute' FROM public.lfg_swipe_sessions WHERE user_id = @id", new { id = a.Id }))
            .Should().BeTrue();
    }

    // ── lobby mode ────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> OpenLobby(Player host, int slots = 2, string mode = "competitive", string area = "south-asia") =>
        host.Client.PostAsJsonAsync(Lobbies, new { open_slots = slots, title = "Need smokes", wanted_roles = new[] { "controller" }, mode, area });

    private static async Task<Guid> LobbyId(HttpResponseMessage created)
    {
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(created)).GetProperty("id").GetGuid();
    }

    private static async Task<List<Guid>> Board(Player p, string area = "south-asia") =>
        (await Json(await p.Client.GetAsync($"{Lobbies}?mode=competitive&area={area}"))).EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task Lobby_OnePerHost_AndTheBoardOnlyShowsCompatibleLobbies()
    {
        var host = await NewPlayer();
        var lobby = await LobbyId(await OpenLobby(host));
        (await Error(await OpenLobby(host))).Should().Be("LOBBY_EXISTS");

        var sameArea = await NewPlayer();
        var otherArea = await NewPlayer("AE");
        var unrated = await NewPlayer();
        (await Board(sameArea)).Should().Contain(lobby);
        (await Board(otherArea, "middle-east")).Should().NotContain(lobby, "the lobby is in South Asia");
        (await Json(await unrated.Client.GetAsync($"{Lobbies}?mode=unrated&area=south-asia"))).EnumerateArray()
            .Select(l => l.GetProperty("id").GetGuid()).Should().NotContain(lobby);
        (await Board(host)).Should().NotContain(lobby, "hosts don't see their own lobby on the board");
    }

    [Fact]
    public async Task Lobby_RequestAcceptAndFill_OpensAGroupChat_AndNotifiesEveryone()
    {
        var host = await NewPlayer();
        var lobby = await LobbyId(await OpenLobby(host, slots: 2));
        var a = await NewPlayer();
        var b = await NewPlayer();
        (await Error(await a.Client.PostAsJsonAsync($"{Lobbies}/{lobby}/requests", new { area = "europe" }))).Should().Be("LOBBY_NOT_FOUND", "a player from another area can't join");
        (await a.Client.PostAsJsonAsync($"{Lobbies}/{lobby}/requests", new { role_key = "controller", area = "south-asia" })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Error(await a.Client.PostAsJsonAsync($"{Lobbies}/{lobby}/requests", new { }))).Should().Be("ALREADY_REQUESTED");
        (await b.Client.PostAsJsonAsync($"{Lobbies}/{lobby}/requests", new { })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Scalar<int>("SELECT count(*)::int FROM public.notifications WHERE user_id = @id AND type::text = 'squad_up_lobby_request'", new { id = host.Id }))
            .Should().Be(2);

        var mine = await Json(await host.Client.GetAsync($"{Lobbies}/mine"));
        mine.GetProperty("members").GetArrayLength().Should().Be(2);

        (await host.Client.PostAsync($"{Lobbies}/{lobby}/requests/{a.Id}/accept", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.Client.PostAsync($"{Lobbies}/{lobby}/requests/{b.Id}/accept", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Scalar<string>("SELECT status FROM public.squad_up_lobbies WHERE id = @lobby", new { lobby })).Should().Be("full");
        var conversation = await Scalar<Guid>("SELECT conversation_id FROM public.squad_up_lobbies WHERE id = @lobby", new { lobby });
        (await Scalar<string>("SELECT type FROM public.conversations WHERE id = @conversation", new { conversation })).Should().Be("group");
        (await Scalar<int>("SELECT count(*)::int FROM public.conversation_participants WHERE conversation_id = @conversation AND is_active", new { conversation }))
            .Should().Be(3);
        foreach (var player in new[] { host, a, b })
            (await Scalar<int>("SELECT count(*)::int FROM public.notifications WHERE user_id = @id AND type::text = 'squad_up_lobby_full'", new { id = player.Id }))
                .Should().Be(1);
        (await Board(await NewPlayer())).Should().NotContain(lobby, "full lobbies leave the board");
        var joined = await Json(await a.Client.GetAsync($"{Lobbies}/mine"));
        joined.GetProperty("joined").EnumerateArray().Single().GetProperty("conversationId").GetGuid().Should().Be(conversation);
    }

    [Fact]
    public async Task Lobby_TheLastSlotCannotBeFilledTwice()
    {
        var host = await NewPlayer();
        var lobby = await LobbyId(await OpenLobby(host, slots: 1));
        var a = await NewPlayer();
        var b = await NewPlayer();
        await a.Client.PostAsJsonAsync($"{Lobbies}/{lobby}/requests", new { });
        await b.Client.PostAsJsonAsync($"{Lobbies}/{lobby}/requests", new { });
        var results = await Task.WhenAll(
            host.Client.PostAsync($"{Lobbies}/{lobby}/requests/{a.Id}/accept", null),
            host.Client.PostAsync($"{Lobbies}/{lobby}/requests/{b.Id}/accept", null));
        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        (await Scalar<int>("SELECT count(*)::int FROM public.squad_up_lobby_members WHERE lobby_id = @lobby AND status = 'accepted'", new { lobby }))
            .Should().Be(1);
    }

    [Fact]
    public async Task Lobby_LeavingReopensIt_AndKickedPlayersCannotComeBack()
    {
        var host = await NewPlayer();
        var lobby = await LobbyId(await OpenLobby(host, slots: 1));
        var a = await NewPlayer();
        await a.Client.PostAsJsonAsync($"{Lobbies}/{lobby}/requests", new { });
        await host.Client.PostAsync($"{Lobbies}/{lobby}/requests/{a.Id}/accept", null);
        (await a.Client.PostAsync($"{Lobbies}/{lobby}/leave", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Scalar<string>("SELECT status FROM public.squad_up_lobbies WHERE id = @lobby", new { lobby })).Should().Be("open");
        (await Scalar<bool>(
            "SELECT is_active FROM public.conversation_participants cp JOIN public.squad_up_lobbies l ON l.conversation_id = cp.conversation_id WHERE l.id = @lobby AND cp.user_id = @id",
            new { lobby, id = a.Id })).Should().BeFalse();

        var b = await NewPlayer();
        await b.Client.PostAsJsonAsync($"{Lobbies}/{lobby}/requests", new { });
        (await host.Client.PostAsync($"{Lobbies}/{lobby}/kick/{b.Id}", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var again = await b.Client.PostAsJsonAsync($"{Lobbies}/{lobby}/requests", new { });
        again.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Error(again)).Should().Be("REMOVED_FROM_LOBBY");
    }

    [Fact]
    public async Task Lobby_DisappearsWhenTheHostGoesQuiet_AndHostHeartbeatsKeepItAlive()
    {
        var host = await NewPlayer();
        var lobby = await LobbyId(await OpenLobby(host));
        var viewer = await NewPlayer();
        (await host.Client.PostAsync($"{Swipe}/session/heartbeat", null)).StatusCode.Should().Be(HttpStatusCode.OK, "a hosted lobby counts as presence");
        await Exec("UPDATE public.squad_up_lobbies SET last_heartbeat = now() - interval '5 minutes' WHERE id = @lobby", new { lobby });
        (await Board(viewer)).Should().NotContain(lobby);
        await host.Client.GetAsync($"{Lobbies}/mine");
        (await Scalar<string>("SELECT status FROM public.squad_up_lobbies WHERE id = @lobby", new { lobby })).Should().Be("closed");
    }

    [Fact]
    public async Task Lobby_RestrictedPlayersCannotHostOrJoin_AndBlockedHostsAreHidden()
    {
        var restricted = await NewPlayer();
        await Exec("INSERT INTO public.squad_up_restrictions (user_id, until, reason_category) VALUES (@id, now() + interval '1 day', 'harassment')",
            new { id = restricted.Id });
        (await Error(await OpenLobby(restricted))).Should().Be("SQUAD_UP_RESTRICTED");

        var host = await NewPlayer();
        var lobby = await LobbyId(await OpenLobby(host));
        var joinRestricted = await restricted.Client.PostAsJsonAsync($"{Lobbies}/{lobby}/requests", new { });
        joinRestricted.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var blocker = await NewPlayer();
        await Exec("INSERT INTO public.squad_up_blocks (blocker_id, blocked_id) VALUES (@blocker, @host)", new { blocker = blocker.Id, host = host.Id });
        (await Board(blocker)).Should().NotContain(lobby);
        (await blocker.Client.PostAsJsonAsync($"{Lobbies}/{lobby}/requests", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lobby_MembersCanReportEachOther_WithLobbyContext()
    {
        var host = await NewPlayer();
        var lobby = await LobbyId(await OpenLobby(host));
        var member = await NewPlayer();
        await member.Client.PostAsJsonAsync($"{Lobbies}/{lobby}/requests", new { });
        var form = new MultipartFormDataContent
        {
            { new StringContent(host.Id.ToString()), "reported_user_id" },
            { new StringContent("harassment"), "reason" },
            { new StringContent("Rude in the lobby chat"), "details" },
            { new StringContent(lobby.ToString()), "lobby_id" },
            { new StringContent("false"), "hide_player" },
        };
        var response = await member.Client.PostAsync($"{Swipe}/reports", form);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(response)).GetProperty("id").GetGuid();
        (await Scalar<string>("SELECT context FROM public.squad_up_reports WHERE id = @id", new { id })).Should().Be("lobby");
    }

    // ── public page and admin ─────────────────────────────────────────────────

    [Fact]
    public async Task PublicStats_WorkWithoutSigningIn()
    {
        var response = await factory.CreateClient().GetAsync($"{Swipe}/public-stats");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stats = await Json(response);
        stats.GetProperty("playersLive").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        stats.TryGetProperty("openLobbies", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Admin_SeesOpenLobbies_AndOnlyManagersCanCloseThem()
    {
        var host = await NewPlayer();
        var lobby = await LobbyId(await OpenLobby(host));
        var viewer = await AdminWith("squad_up:view");
        var manager = await AdminWith("squad_up:view", "squad_up:manage");
        (await Json(await viewer.GetAsync("/api/admin/squad-up/lobbies"))).EnumerateArray()
            .Should().Contain(l => l.GetProperty("id").GetGuid() == lobby);
        (await viewer.PostAsync($"/api/admin/squad-up/lobbies/{lobby}/close", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.PostAsync($"/api/admin/squad-up/lobbies/{lobby}/close", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Scalar<string>("SELECT status FROM public.squad_up_lobbies WHERE id = @lobby", new { lobby })).Should().Be("closed");
    }

    private async Task<HttpClient> AdminWith(params string[] permissions)
    {
        var id = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(id);
        var key = $"squad-flag-{id:N}";
        await Exec(
            """
            WITH role AS (INSERT INTO public.admin_roles (name, key, description) VALUES (@key, @key, 'test') RETURNING id)
            INSERT INTO public.admin_user_roles (user_id, role_id) SELECT @id, id FROM role;
            INSERT INTO public.admin_role_permissions (role_id, permission_id)
            SELECT r.id, p.id FROM public.admin_roles r, public.admin_permissions p WHERE r.key = @key AND p.name = ANY(@permissions);
            """, new { id, key, permissions });
        return factory.CreateAuthenticatedClient(id);
    }
}
