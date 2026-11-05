using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

[Collection(IntegrationCollection.Name)]
public sealed class LfgSwipeEndpointsTests(ApiFactory factory) : IAsyncLifetime
{
    private const string Base = "/api/competitive/lfg/swipe";
    private const string OwnRegion = "@own";

    // The suite shares one database, so each test gets private regions: its pool holds only its own players
    // and a limited, random card sample can never miss the player the test is looking for.
    private readonly string region = $"t{Guid.NewGuid():N}"[..10];
    private readonly string altRegion = $"o{Guid.NewGuid():N}"[..10];

    public async Task InitializeAsync() => await factory.Seeder.SeedFeatureFlagAsync("squad_up", enabled: true);
    public Task DisposeAsync() => Task.CompletedTask;

    // ── helpers ───────────────────────────────────────────────────────────────

    private sealed record Player(Guid Id, HttpClient Client);

    private async Task<Player> NewPlayer(string? region = OwnRegion, int? tier = 15, string agents = "[]", bool linked = true)
    {
        region = region == OwnRegion ? this.region : region;
        var id = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(id);
        await using (var countryConn = factory.Seeder.OpenConnection())
            await countryConn.ExecuteAsync("UPDATE public.profiles SET country_code = 'PK' WHERE id = @id", new { id });
        if (linked)
        {
            await using var conn = factory.Seeder.OpenConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line, region)
                VALUES (@id, @puuid, 'Player', 'TAG', @region) ON CONFLICT (user_id) DO NOTHING;
                INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, competitive_tier, competitive_tier_name,
                    win_rate, kd_ratio, hs_pct, headshot_data_complete, matches_analyzed, popular_agents, sync_status, synced_at)
                VALUES (@id, 'valorant', @puuid, @tier, 'Gold 1', 52.5, 1.1, 22.5, true, 30, @agents::jsonb, 'complete', now())
                ON CONFLICT (user_id, game_slug) DO NOTHING;
                """, new { id, puuid = $"puuid-{id}", region, tier, agents });
        }
        return new Player(id, factory.CreateAuthenticatedClient(id));
    }

    private static Task<HttpResponseMessage> Start(Player p, int slots = 1, bool mic = true) =>
        p.Client.PostAsJsonAsync($"{Base}/session", new { mic_on = mic, slots_needed = slots, area = "south-asia" });

    private async Task<Player> Queued(string? region = OwnRegion, int? tier = 15, string agents = "[]", int slots = 1)
    {
        var p = await NewPlayer(region, tier, agents);
        (await Start(p, slots)).StatusCode.Should().Be(HttpStatusCode.OK);
        return p;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static Task<HttpResponseMessage> Right(Player from, Player to) => from.Client.PostAsync($"{Base}/{to.Id}/right", null);
    private static Task<HttpResponseMessage> Left(Player from, Player to) => from.Client.PostAsync($"{Base}/{to.Id}/left", null);

    private static async Task<List<JsonElement>> Cards(Player p, int limit = 10, int tierRange = 8) =>
        (await Json(await p.Client.GetAsync($"{Base}/cards?limit={limit}&tier_range={tierRange}"))).EnumerateArray().ToList();

    private async Task<T> Scalar<T>(string sql, object args)
    {
        await using var conn = factory.Seeder.OpenConnection();
        return await conn.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("null scalar");
    }

    private async Task Exec(string sql, object args)
    {
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync(sql, args);
    }

    private Task MakeStale(Player p) =>
        Exec("UPDATE public.lfg_swipe_sessions SET last_heartbeat = now() - interval '10 minutes' WHERE user_id = @id", new { id = p.Id });

    private Task<int> Notifications(Player p) =>
        Scalar<int>("SELECT count(*)::int FROM public.notifications WHERE type::text = 'lfg_swipe_match' AND user_id = @id", new { id = p.Id });

    private async Task<string> AgentOfRole(string role) =>
        await Scalar<string>("SELECT agent_id FROM public.game_catalog_agents WHERE game_slug = 'valorant' AND role_key = @role ORDER BY agent_id LIMIT 1", new { role });

    private static string Agents(params string[] ids) =>
        JsonSerializer.Serialize(ids.Select((id, i) => new { agent_id = id, games_played = 30 - i }));

    // ── start session ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_WithoutLinkedRiot_Is422()
    {
        var p = await NewPlayer(linked: false);
        var r = await Start(p);
        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(r)).GetProperty("error").GetString().Should().Be("RIOT_NOT_LINKED");
    }

    [Fact]
    public async Task Start_Unranked_Is422()
    {
        var p = await NewPlayer(tier: null);
        (await Json(await Start(p))).GetProperty("error").GetString().Should().Be("NOT_RANKED");
    }

    [Fact]
    public async Task Start_WithoutRegion_Is422()
    {
        var p = await NewPlayer(region: null);
        (await Json(await Start(p))).GetProperty("error").GetString().Should().Be("REGION_UNKNOWN");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task Start_SlotsOutOfRange_Is400(int slots)
    {
        var p = await NewPlayer();
        (await Start(p, slots)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_LiveRestart_KeepsSessionAndSwipes_StaleRestart_ClearsOldLikes()
    {
        var a = await Queued();
        var b = await Queued();
        await Right(a, b);
        var first = (await Json(await Start(a))).GetProperty("id").GetGuid();
        (await Scalar<int>("SELECT count(*)::int FROM public.lfg_swipe_actions WHERE session_id = @first", new { first })).Should().Be(1);

        await MakeStale(a);
        var second = (await Json(await Start(a))).GetProperty("id").GetGuid();
        second.Should().NotBe(first);
        (await Scalar<int>("SELECT count(*)::int FROM public.lfg_swipe_actions WHERE target_user_id = @b", new { b = b.Id })).Should().Be(0);
    }

    // ── cards ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cards_WithoutSession_Is404()
    {
        var p = await NewPlayer();
        (await p.Client.GetAsync($"{Base}/cards")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cards_OnlySameRegionAndBand_ExcludesSelfStaleSeenAndSuspended()
    {
        var me = await Queued(tier: 15);
        var near = await Queued(tier: 17);
        var far = await Queued(tier: 24);
        var otherRegion = await Queued(region: altRegion);
        var stale = await Queued();
        var seen = await Queued();
        var suspended = await Queued();
        await MakeStale(stale);
        await Exec("UPDATE public.profiles SET is_suspended = true WHERE id = @id", new { id = suspended.Id });
        (await Left(me, seen)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var ids = (await Cards(me)).Select(c => c.GetProperty("userId").GetGuid()).ToList();
        ids.Should().Contain(near.Id);
        ids.Should().NotContain([me.Id, far.Id, otherRegion.Id, stale.Id, seen.Id, suspended.Id]);
    }

    [Fact]
    public async Task Cards_HideRiotIdWhenPrivacyOff_AndNeverDuplicateAcrossCatalogVersions()
    {
        var me = await Queued();
        var other = await Queued();
        await Exec("UPDATE public.profiles SET privacy_settings = '{\"show_riot_account\": false}'::jsonb WHERE id = @id", new { id = other.Id });
        var hash = $"swipe-{Guid.NewGuid()}";
        await Exec(
            """
            INSERT INTO public.game_catalog_versions (catalog_version, schema_version, content_hash, status, is_active)
            VALUES ('swipe-test', 1, @hash, 'failed', false);
            INSERT INTO public.game_catalog_rank_tiers (version_id, game_slug, tier_key, display_name, sort_order)
            SELECT v.id, 'valorant', 'swipe_test_' || v.id::text, 'Dup', 12
            FROM public.game_catalog_versions v WHERE v.content_hash = @hash OR v.is_active;
            """, new { hash });
        try
        {
            var cards = await Cards(me);
            cards.Count(c => c.GetProperty("userId").GetGuid() == other.Id).Should().Be(1);
            var card = cards.Single(c => c.GetProperty("userId").GetGuid() == other.Id);
            card.GetProperty("riotGameName").ValueKind.Should().Be(JsonValueKind.Null);
            card.GetProperty("riotTagLine").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally
        {
            await Exec(
                """
                DELETE FROM public.game_catalog_rank_tiers WHERE tier_key LIKE 'swipe_test_%';
                DELETE FROM public.game_catalog_versions WHERE content_hash = @hash;
                """, new { hash });
        }
    }

    [Fact]
    public async Task Cards_SynergyLabels_FollowTopAgentRoles()
    {
        var duelist = await AgentOfRole("duelist");
        var controller = await AgentOfRole("controller");
        var me = await Queued(agents: Agents(duelist));
        var complementary = await Queued(agents: Agents(controller));
        var overlapping = await Queued(agents: Agents(duelist));
        var unknown = await Queued(agents: "[]");
        var broken = await Queued(agents: "{\"not\":\"an array\"}");

        var cards = (await Cards(me)).ToDictionary(c => c.GetProperty("userId").GetGuid());
        cards[complementary.Id].GetProperty("synergyLabel").GetString().Should().Be("great_synergy");
        cards[complementary.Id].GetProperty("synergyScore").GetInt32().Should().Be(2);
        cards[overlapping.Id].GetProperty("synergyLabel").GetString().Should().Be("role_overlap");
        cards[unknown.Id].GetProperty("synergyLabel").GetString().Should().Be("unknown");
        cards[broken.Id].GetProperty("synergyLabel").GetString().Should().Be("unknown");
    }

    [Fact]
    public async Task Cards_PutPlayersWhoLikedYouFirst_AndPendingLikesTracksThem()
    {
        var me = await Queued(slots: 3);
        var others = new List<Player>();
        for (var i = 0; i < 4; i++) others.Add(await Queued(slots: 3));
        var liker = others[3];
        (await Json(await Right(liker, me))).GetProperty("matched").GetBoolean().Should().BeFalse();

        var first = (await Cards(me)).First();
        first.GetProperty("userId").GetGuid().Should().Be(liker.Id);
        first.GetProperty("likedYou").GetBoolean().Should().BeTrue();
        (await Json(await me.Client.GetAsync($"{Base}/pending-likes"))).GetProperty("count").GetInt32().Should().Be(1);
        (await Json(await me.Client.PostAsync($"{Base}/session/heartbeat", null))).GetProperty("pendingLikes").GetInt32().Should().Be(1);

        (await Json(await Right(me, liker))).GetProperty("matched").GetBoolean().Should().BeTrue();
        (await Json(await me.Client.GetAsync($"{Base}/pending-likes"))).GetProperty("count").GetInt32().Should().Be(0);
    }

    // ── swiping and matching ──────────────────────────────────────────────────

    [Fact]
    public async Task Right_OnSelf_Is400_AndOutsidePool_Is404()
    {
        var a = await Queued();
        (await Right(a, a)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var farTier = await Queued(tier: 27);
        var otherRegion = await Queued(region: altRegion);
        var noSession = await NewPlayer();
        foreach (var target in new[] { farTier, otherRegion, noSession })
        {
            var r = await Right(a, target);
            r.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await Json(r)).GetProperty("error").GetString().Should().Be("TARGET_NOT_ACTIVE");
        }
    }

    [Fact]
    public async Task Right_WithoutOwnSession_Is404()
    {
        var a = await NewPlayer();
        var b = await Queued();
        (await Json(await Right(a, b))).GetProperty("error").GetString().Should().Be("NO_ACTIVE_SESSION");
    }

    [Fact]
    public async Task MutualRight_CreatesOneMatch_OneConversation_TwoNotifications_AndRepeatsAreIdempotent()
    {
        var a = await Queued(slots: 2);
        var b = await Queued(slots: 2);
        (await Json(await Right(a, b))).GetProperty("matched").GetBoolean().Should().BeFalse();
        var result = await Json(await Right(b, a));
        result.GetProperty("matched").GetBoolean().Should().BeTrue();
        var match = result.GetProperty("match");
        match.GetProperty("matchedUserId").GetGuid().Should().Be(a.Id);
        var conversation = match.GetProperty("conversationId").GetGuid();
        (await Notifications(a)).Should().Be(1);
        (await Notifications(b)).Should().Be(1);

        var again = await Json(await Right(b, a));
        again.GetProperty("matched").GetBoolean().Should().BeTrue();
        again.GetProperty("match").GetProperty("matchId").GetGuid().Should().Be(match.GetProperty("matchId").GetGuid());
        (await Json(await Right(a, b))).GetProperty("match").GetProperty("matchId").GetGuid().Should().Be(match.GetProperty("matchId").GetGuid());
        (await Scalar<int>("SELECT count(*)::int FROM public.lfg_swipe_matches WHERE user1_id = LEAST(@a, @b) AND user2_id = GREATEST(@a, @b)", new { a = a.Id, b = b.Id })).Should().Be(1);
        (await Notifications(a)).Should().Be(1);
        (await Scalar<int>("SELECT count(*)::int FROM public.conversation_participants WHERE conversation_id = @conversation", new { conversation })).Should().Be(2);
        (await Scalar<int>("SELECT slots_needed FROM public.lfg_swipe_sessions WHERE user_id = @id", new { id = a.Id })).Should().Be(1);
    }

    [Fact]
    public async Task Match_ReusesExistingDirectConversation()
    {
        var a = await Queued();
        var b = await Queued();
        var existing = Guid.NewGuid();
        await Exec(
            """
            INSERT INTO public.conversations (id, type, created_by) VALUES (@existing, 'direct', @a);
            INSERT INTO public.conversation_participants (conversation_id, user_id) VALUES (@existing, @a), (@existing, @b);
            """, new { existing, a = a.Id, b = b.Id });
        await Right(a, b);
        var match = (await Json(await Right(b, a))).GetProperty("match");
        match.GetProperty("conversationId").GetGuid().Should().Be(existing);
    }

    [Fact]
    public async Task FirstDecisionStands_RightAfterLeftNeverMatches()
    {
        var a = await Queued();
        var b = await Queued();
        (await Left(a, b)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await Right(b, a);
        (await Json(await Right(a, b))).GetProperty("matched").GetBoolean().Should().BeFalse();
        (await Cards(a)).Select(c => c.GetProperty("userId").GetGuid()).Should().NotContain(b.Id);
    }

    [Fact]
    public async Task FillingLastSlot_EndsSession_KeepsStats_AndHeartbeatReturns404()
    {
        var a = await Queued(slots: 1);
        var b = await Queued(slots: 2);
        await Right(a, b);
        var result = await Json(await Right(b, a));
        result.GetProperty("sessionEnded").GetBoolean().Should().BeFalse();

        var aStatus = await a.Client.PostAsync($"{Base}/session/heartbeat", null);
        aStatus.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await b.Client.PostAsync($"{Base}/session/heartbeat", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var stats = await Json(await a.Client.GetAsync($"{Base}/session/stats"));
        stats.GetProperty("swipesRight").GetInt32().Should().Be(1);
        stats.GetProperty("matchesCount").GetInt32().Should().Be(1);
        (await Cards(b)).Select(c => c.GetProperty("userId").GetGuid()).Should().NotContain(a.Id);
    }

    [Fact]
    public async Task LastSlotFilled_ByCaller_ReturnsSessionEndedWithStats()
    {
        var a = await Queued(slots: 1);
        var b = await Queued(slots: 1);
        await Right(b, a);
        var result = await Json(await Right(a, b));
        result.GetProperty("sessionEnded").GetBoolean().Should().BeTrue();
        result.GetProperty("stats").GetProperty("matchesCount").GetInt32().Should().Be(1);
    }

    // ── stats, end session, matches ───────────────────────────────────────────

    [Fact]
    public async Task Stats_CountSwipesOnce_AndDeleteReturnsSummary()
    {
        var me = await Queued(slots: 4);
        var x = await Queued();
        var y = await Queued();
        var z = await Queued();
        await Right(me, x);
        await Right(me, x);
        await Right(me, y);
        await Left(me, z);
        var stats = await Json(await me.Client.GetAsync($"{Base}/session/stats"));
        stats.GetProperty("swipesRight").GetInt32().Should().Be(2);
        stats.GetProperty("swipesLeft").GetInt32().Should().Be(1);
        stats.GetProperty("matchesCount").GetInt32().Should().Be(0);

        var ended = await me.Client.DeleteAsync($"{Base}/session");
        ended.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(ended)).GetProperty("swipesRight").GetInt32().Should().Be(2);
        (await me.Client.GetAsync($"{Base}/session/stats")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Cards(x)).Select(c => c.GetProperty("userId").GetGuid()).Should().NotContain(me.Id);
    }

    [Fact]
    public async Task Matches_VisibleOnlyToTheTwoParties()
    {
        var a = await Queued();
        var b = await Queued();
        var outsider = await Queued();
        await Right(a, b);
        await Right(b, a);
        (await Json(await a.Client.GetAsync($"{Base}/matches"))).GetArrayLength().Should().Be(1);
        (await Json(await b.Client.GetAsync($"{Base}/matches"))).GetArrayLength().Should().Be(1);
        (await Json(await outsider.Client.GetAsync($"{Base}/matches"))).GetArrayLength().Should().Be(0);
    }

    // ── data and RLS ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentCatalog_HasValidUuidsAndAllowedRoles()
    {
        await using var conn = factory.Seeder.OpenConnection();
        var rows = (await conn.QueryAsync<(string AgentId, string Role)>(
            "SELECT agent_id, role_key FROM public.game_catalog_agents WHERE game_slug = 'valorant'")).ToList();
        rows.Count.Should().BeGreaterThanOrEqualTo(24);
        rows.All(r => Guid.TryParse(r.AgentId, out var _)).Should().BeTrue();
        rows.Select(r => r.Role).Distinct().Should().BeEquivalentTo("duelist", "initiator", "controller", "sentinel");
    }

    [Theory]
    [InlineData("SELECT count(*) FROM public.lfg_swipe_actions")]
    [InlineData("INSERT INTO public.lfg_swipe_sessions (user_id, game_slug, region, competitive_tier) VALUES (gen_random_uuid(), 'valorant', 'na', 10)")]
    [InlineData("DELETE FROM public.lfg_swipe_matches")]
    [InlineData("UPDATE public.game_catalog_agents SET role_key = 'duelist'")]
    public async Task AuthenticatedRole_CannotReadLikesOrWriteQueueTables(string sql)
    {
        await using var conn = factory.Seeder.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("SET LOCAL ROLE authenticated", transaction: tx);
        var act = async () => await conn.ExecuteAsync(sql, transaction: tx);
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }
}
