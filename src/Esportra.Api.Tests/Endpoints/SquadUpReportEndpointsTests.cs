using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Services;
using Esportra.Api.Tests.Infrastructure;
using Esportra.Infrastructure.Supabase;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

[Collection(IntegrationCollection.Name)]
public sealed class SquadUpReportEndpointsTests(ApiFactory factory) : IAsyncLifetime
{
    private const string Swipe = "/api/competitive/lfg/swipe";
    private const string Admin = "/api/admin/squad-up";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x00];

    // Fakes are shared by every test in this class (one host); each test uses its own region and users.
    private static readonly FakeProofStore Store = new();
    private static readonly ISupabaseAdminClient Supabase = Substitute.For<ISupabaseAdminClient>();
    private static WebApplicationFactory<Program>? host;
    private static readonly object HostGate = new();

    private readonly string region = $"t{Guid.NewGuid():N}"[..10];

    public async Task InitializeAsync() => await factory.Seeder.SeedFeatureFlagAsync("squad_up", enabled: true);
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class FakeProofStore : IReportProofStore
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public bool FailUploads;
        public Task<bool> UploadAsync(string path, byte[] data, string contentType, CancellationToken ct)
        {
            if (FailUploads) return Task.FromResult(false);
            lock (Files) Files[path] = data;
            return Task.FromResult(true);
        }
        public Task DeleteAsync(IReadOnlyCollection<string> paths, CancellationToken ct)
        {
            lock (Files) foreach (var path in paths) Files.Remove(path);
            return Task.CompletedTask;
        }
        public Task<string?> CreateSignedUrlAsync(string path, TimeSpan lifetime, CancellationToken ct) =>
            Task.FromResult<string?>($"https://signed.test/{path}?ttl={(int)lifetime.TotalSeconds}");
    }

    private HttpClient As(Guid userId)
    {
        lock (HostGate)
        {
            host ??= factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IReportProofStore>();
                s.AddSingleton<IReportProofStore>(Store);
                s.RemoveAll<ISupabaseAdminClient>();
                s.AddSingleton(Supabase);
            }));
        }
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestAuthHelper.BearerHeader(userId));
        return client;
    }

    private sealed record Player(Guid Id, HttpClient Client);

    private async Task<Player> Queued(int slots = 3, int tier = 15)
    {
        var id = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(id);
        await using (var countryConn = factory.Seeder.OpenConnection())
            await countryConn.ExecuteAsync("UPDATE public.profiles SET country_code = 'PK' WHERE id = @id", new { id });
        await Exec(
            """
            INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line, region) VALUES (@id, @puuid, 'P', 'T', @region);
            INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, competitive_tier, competitive_tier_name, sync_status, synced_at)
            VALUES (@id, 'valorant', @puuid, @tier, 'Gold 1', 'complete', now());
            """, new { id, puuid = $"puuid-{id}", region, tier });
        var player = new Player(id, As(id));
        (await player.Client.PostAsJsonAsync($"{Swipe}/session", new { mic_on = true, slots_needed = slots, area = "south-asia" })).EnsureSuccessStatusCode();
        return player;
    }

    private async Task<HttpClient> AdminWith(params string[] permissions)
    {
        var id = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(id);
        if (permissions.Length > 0)
        {
            var key = $"squad-report-{id:N}";
            // The original RBAC seed is not part of the replay schema, so make sure each permission exists.
            foreach (var permission in permissions)
                await Exec(
                    "INSERT INTO public.admin_permissions (name, description, resource, action) VALUES (@name, @name, split_part(@name, ':', 1), split_part(@name, ':', 2)) ON CONFLICT (name) DO NOTHING",
                    new { name = permission });
            await Exec(
                """
                WITH role AS (INSERT INTO public.admin_roles (name, key, description) VALUES (@key, @key, 'test') RETURNING id)
                INSERT INTO public.admin_user_roles (user_id, role_id) SELECT @id, id FROM role;
                INSERT INTO public.admin_role_permissions (role_id, permission_id)
                SELECT r.id, p.id FROM public.admin_roles r, public.admin_permissions p WHERE r.key = @key AND p.name = ANY(@permissions);
                """, new { id, key, permissions });
        }
        return As(id);
    }

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

    private static MultipartFormDataContent Form(Guid reported, string reason = "harassment", string details = "They kept insulting me",
        int images = 1, bool hide = true, string? link = null, Guid? match = null, byte[]? imageBytes = null, string imageName = "proof.png")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(reported.ToString()), "reported_user_id" },
            { new StringContent(reason), "reason" },
            { new StringContent(details), "details" },
            { new StringContent(hide ? "true" : "false"), "hide_player" },
        };
        if (link is not null) form.Add(new StringContent(link), "proof_link");
        if (match is not null) form.Add(new StringContent(match.ToString()!), "match_id");
        for (var i = 0; i < images; i++) form.Add(new ByteArrayContent(imageBytes ?? Png), "files", imageName);
        return form;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task<string?> Error(HttpResponseMessage r) => (await Json(r)).GetProperty("error").GetString();

    private async Task<(Player Reporter, Player Reported, Guid ReportId)> OpenReport(string reason = "harassment")
    {
        var reporter = await Queued();
        var reported = await Queued();
        var response = await reporter.Client.PostAsync($"{Swipe}/reports", Form(reported.Id, reason));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (reporter, reported, (await Json(response)).GetProperty("id").GetGuid());
    }

    private Task<int> Notifications(Guid userId, string type) =>
        Scalar<int>("SELECT count(*)::int FROM public.notifications WHERE user_id = @userId AND type::text = @type", new { userId, type });

    // ── submitting a report ───────────────────────────────────────────────────

    [Fact]
    public async Task Report_StoresPrivateEvidence_HidesThePlayerBothWays_AndAlertsAdmins()
    {
        var reporter = await Queued();
        var reported = await Queued();
        var response = await reporter.Client.PostAsync($"{Swipe}/reports", Form(reported.Id, images: 2, link: "https://clips.example/abc"));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(response)).GetProperty("id").GetGuid();

        (await Scalar<int>("SELECT count(*)::int FROM public.squad_up_report_evidence WHERE report_id = @id", new { id })).Should().Be(2);
        lock (Store.Files) Store.Files.Keys.Count(k => k.StartsWith($"reports/{id}/")).Should().Be(2);
        (await Scalar<string>("SELECT snapshot->>'username' FROM public.squad_up_reports WHERE id = @id", new { id })).Should().NotBeNullOrEmpty();
        (await Scalar<int>("SELECT count(*)::int FROM public.admin_alerts WHERE type = 'squad_up_report' AND data->>'report_id' = @id", new { id = id.ToString() })).Should().Be(1);

        var cards = (await Json(await reporter.Client.GetAsync($"{Swipe}/cards?limit=10"))).EnumerateArray().Select(c => c.GetProperty("userId").GetGuid());
        cards.Should().NotContain(reported.Id);
        var theirCards = (await Json(await reported.Client.GetAsync($"{Swipe}/cards?limit=10"))).EnumerateArray().Select(c => c.GetProperty("userId").GetGuid());
        theirCards.Should().NotContain(reporter.Id);
        (await reported.Client.PostAsync($"{Swipe}/{reporter.Id}/right", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await reporter.Client.PostAsync($"{Swipe}/{reported.Id}/right", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Report_WithoutHide_StillRemovesThePlayerFromTheDeck_ButCreatesNoBlock()
    {
        var reporter = await Queued();
        var reported = await Queued();
        (await reporter.Client.PostAsync($"{Swipe}/reports", Form(reported.Id, hide: false))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Scalar<int>("SELECT count(*)::int FROM public.squad_up_blocks WHERE blocker_id = @id", new { id = reporter.Id })).Should().Be(0);
        var cards = (await Json(await reporter.Client.GetAsync($"{Swipe}/cards?limit=10"))).EnumerateArray().Select(c => c.GetProperty("userId").GetGuid());
        cards.Should().NotContain(reported.Id);
    }

    [Theory]
    [InlineData("bad_reason", "ok details", 1, "https://x.test", "INVALID_REASON")]
    [InlineData("other", "short", 1, null, "DETAILS_REQUIRED")]
    [InlineData("harassment", "details", 1, "http://insecure.test", "INVALID_PROOF_LINK")]
    [InlineData("harassment", "details", 6, null, "TOO_MANY_FILES")]
    public async Task Report_RejectsInvalidInput_BeforeStoringAnything(string reason, string details, int images, string? link, string expected)
    {
        var reporter = await Queued();
        var reported = await Queued();
        var before = Store.Files.Count;
        var response = await reporter.Client.PostAsync($"{Swipe}/reports", Form(reported.Id, reason, details, images, link: link));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Error(response)).Should().Be(expected);
        Store.Files.Count.Should().Be(before);
    }

    [Fact]
    public async Task Report_RejectsSelf_NonImages_AndFilesWhoseContentLiesAboutTheirType()
    {
        var reporter = await Queued();
        var reported = await Queued();
        (await Error(await reporter.Client.PostAsync($"{Swipe}/reports", Form(reporter.Id)))).Should().Be("SELF_REPORT");
        var fake = await reporter.Client.PostAsync($"{Swipe}/reports", Form(reported.Id, imageBytes: "not an image"u8.ToArray()));
        (await Error(fake)).Should().Be("INVALID_IMAGE");
        var svg = await reporter.Client.PostAsync($"{Swipe}/reports", Form(reported.Id, imageName: "proof.svg"));
        (await Error(svg)).Should().Be("INVALID_IMAGE");
        (await reporter.Client.PostAsync($"{Swipe}/reports", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Report_RequiresARealConnection_AndIsNotRepeatableWithin24Hours()
    {
        var reporter = await Queued();
        var stranger = await NewStranger();
        var response = await reporter.Client.PostAsync($"{Swipe}/reports", Form(stranger));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Error(response)).Should().Be("NOT_REPORTABLE");

        var reported = await Queued();
        (await reporter.Client.PostAsync($"{Swipe}/reports", Form(reported.Id, hide: false))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await reporter.Client.PostAsync($"{Swipe}/reports", Form(reported.Id, hide: false))).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private async Task<Guid> NewStranger()
    {
        var id = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(id);
        return id;
    }

    [Fact]
    public async Task Report_IsLimitedToFivePerHour()
    {
        var reporter = await Queued();
        for (var i = 0; i < 5; i++)
        {
            var other = await Queued();
            (await reporter.Client.PostAsync($"{Swipe}/reports", Form(other.Id, hide: false))).StatusCode.Should().Be(HttpStatusCode.Created);
        }
        var sixth = await Queued();
        var response = await reporter.Client.PostAsync($"{Swipe}/reports", Form(sixth.Id, hide: false));
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await Error(response)).Should().Be("TOO_MANY_REPORTS");
    }

    [Fact]
    public async Task Report_AfterAMatch_IsAllowedEvenWhenBothSessionsEnded()
    {
        var a = await Queued(slots: 1);
        var b = await Queued(slots: 1);
        await a.Client.PostAsync($"{Swipe}/{b.Id}/right", null);
        var match = await Json(await b.Client.PostAsync($"{Swipe}/{a.Id}/right", null));
        var matchId = match.GetProperty("match").GetProperty("matchId").GetGuid();
        (await a.Client.PostAsync($"{Swipe}/session/heartbeat", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var response = await a.Client.PostAsync($"{Swipe}/reports", Form(b.Id, "cheating", match: matchId));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(response)).GetProperty("id").GetGuid();
        (await Scalar<string>("SELECT context FROM public.squad_up_reports WHERE id = @id", new { id })).Should().Be("match");
    }

    [Fact]
    public async Task Report_WhenStorageFails_ReturnsBadGateway_AndKeepsNothing()
    {
        var reporter = await Queued();
        var reported = await Queued();
        Store.FailUploads = true;
        try
        {
            var response = await reporter.Client.PostAsync($"{Swipe}/reports", Form(reported.Id, images: 2));
            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
        finally { Store.FailUploads = false; }
        (await Scalar<int>("SELECT count(*)::int FROM public.squad_up_reports WHERE reporter_id = @id", new { id = reporter.Id })).Should().Be(0);
    }

    [Fact]
    public async Task MyReports_ShowStatusOnly_AndHiddenPlayersCanBeUnhidden()
    {
        var (reporter, reported, _) = await OpenReport();
        var mine = (await Json(await reporter.Client.GetAsync($"{Swipe}/reports/mine"))).EnumerateArray().ToList();
        mine.Should().ContainSingle().Which.GetProperty("status").GetString().Should().Be("open");
        mine[0].TryGetProperty("resolution", out _).Should().BeFalse();

        (await Json(await reporter.Client.GetAsync($"{Swipe}/blocks"))).EnumerateArray().Should().ContainSingle();
        (await reporter.Client.DeleteAsync($"{Swipe}/blocks/{reported.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await reporter.Client.DeleteAsync($"{Swipe}/blocks/{reported.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── admin review ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_Permissions_ViewerReads_ManagerResolves_PlayersAreRejected()
    {
        var (_, _, id) = await OpenReport();
        var none = await AdminWith();
        var viewer = await AdminWith("squad_up:view");
        var manager = await AdminWith("squad_up:view", "squad_up:manage");
        (await none.GetAsync($"{Admin}/reports")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.GetAsync($"{Admin}/reports")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync($"{Admin}/reports/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsJsonAsync($"{Admin}/reports/{id}/resolve", new { action = "dismiss" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.PostAsJsonAsync($"{Admin}/reports/{id}/resolve", new { action = "dismiss" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_Detail_ReturnsShortLivedSignedUrls_Snapshot_AndHistory()
    {
        var (reporter, reported, id) = await OpenReport();
        var viewer = await AdminWith("squad_up:view");
        var detail = await Json(await viewer.GetAsync($"{Admin}/reports/{id}"));
        var evidence = detail.GetProperty("evidence").EnumerateArray().ToList();
        evidence.Should().ContainSingle();
        evidence[0].GetProperty("url").GetString().Should().StartWith("https://signed.test/reports/").And.Contain("ttl=300");
        detail.GetProperty("report").GetProperty("reporterId").GetGuid().Should().Be(reporter.Id);
        detail.GetProperty("report").GetProperty("snapshot").GetProperty("username").GetString().Should().NotBeNullOrEmpty();
        var list = await Json(await viewer.GetAsync($"{Admin}/reports?status=open&limit=100"));
        list.GetProperty("items").EnumerateArray().Should().Contain(r => r.GetProperty("id").GetGuid() == id);
        reported.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Resolve_Dismiss_TellsOnlyTheReporter_AndCannotBeRepeated()
    {
        var (reporter, reported, id) = await OpenReport();
        var manager = await AdminWith("squad_up:view", "squad_up:manage");
        (await manager.PostAsJsonAsync($"{Admin}/reports/{id}/resolve", new { action = "dismiss" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Notifications(reporter.Id, "squad_up_report_resolved")).Should().Be(1);
        (await Notifications(reported.Id, "squad_up_warning")).Should().Be(0);
        (await Scalar<string>("SELECT resolution FROM public.squad_up_reports WHERE id = @id", new { id })).Should().Be("dismissed");
        var again = await manager.PostAsJsonAsync($"{Admin}/reports/{id}/resolve", new { action = "warn", note = "second try" });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Resolve_Warn_NeedsANote_AndNeverNamesTheReporter()
    {
        var (reporter, reported, id) = await OpenReport();
        var manager = await AdminWith("squad_up:view", "squad_up:manage");
        var missing = await manager.PostAsJsonAsync($"{Admin}/reports/{id}/resolve", new { action = "warn" });
        (await Error(missing)).Should().Be("NOTE_REQUIRED");
        (await manager.PostAsJsonAsync($"{Admin}/reports/{id}/resolve", new { action = "warn", note = "Insulted a teammate" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Notifications(reported.Id, "squad_up_warning")).Should().Be(1);
        var message = await Scalar<string>("SELECT message FROM public.notifications WHERE user_id = @id AND type::text = 'squad_up_warning'", new { id = reported.Id });
        var reporterName = await Scalar<string>("SELECT username FROM public.profiles WHERE id = @id", new { id = reporter.Id });
        message.Should().NotContain(reporterName);
        var reporterMessage = await Scalar<string>("SELECT message FROM public.notifications WHERE user_id = @id AND type::text = 'squad_up_report_resolved'", new { id = reporter.Id });
        reporterMessage.Should().Contain("took action").And.NotContain("warn");
    }

    [Fact]
    public async Task Resolve_Ban_RemovesTheSession_BlocksStarting_AndCanBeLifted()
    {
        var (_, reported, id) = await OpenReport();
        var manager = await AdminWith("squad_up:view", "squad_up:manage");
        (await Error(await manager.PostAsJsonAsync($"{Admin}/reports/{id}/resolve", new { action = "squad_up_ban", note = "Repeated abuse", duration_hours = 5 })))
            .Should().Be("INVALID_DURATION");
        (await manager.PostAsJsonAsync($"{Admin}/reports/{id}/resolve", new { action = "squad_up_ban", note = "Repeated abuse", duration_hours = 24 })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        (await reported.Client.PostAsync($"{Swipe}/session/heartbeat", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var start = await reported.Client.PostAsJsonAsync($"{Swipe}/session", new { mic_on = true, slots_needed = 1, area = "south-asia" });
        start.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await Json(start);
        body.GetProperty("error").GetString().Should().Be("SQUAD_UP_RESTRICTED");
        body.GetProperty("until").GetDateTime().Should().BeAfter(DateTime.UtcNow.AddHours(23));
        (await Notifications(reported.Id, "squad_up_restricted")).Should().Be(1);

        var restriction = (await Json(await manager.GetAsync($"{Admin}/restrictions"))).EnumerateArray().Single(r => r.GetProperty("userId").GetGuid() == reported.Id);
        (await manager.PostAsync($"{Admin}/restrictions/{restriction.GetProperty("id").GetGuid()}/lift", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await reported.Client.PostAsJsonAsync($"{Swipe}/session", new { mic_on = true, slots_needed = 1, area = "south-asia" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Resolve_PermanentBan_HasNoEndDate()
    {
        var (_, reported, id) = await OpenReport("hate_speech");
        var manager = await AdminWith("squad_up:view", "squad_up:manage");
        (await manager.PostAsJsonAsync($"{Admin}/reports/{id}/resolve", new { action = "squad_up_ban", note = "Hate speech", duration_hours = 0 })).EnsureSuccessStatusCode();
        (await Scalar<bool>("SELECT until IS NULL FROM public.squad_up_restrictions WHERE user_id = @id", new { id = reported.Id })).Should().BeTrue();
    }

    [Fact]
    public async Task Resolve_Suspend_NeedsUsersBan_AndSuspendsTheAccount()
    {
        var (_, reported, id) = await OpenReport("cheating");
        var withoutBan = await AdminWith("squad_up:view", "squad_up:manage");
        (await withoutBan.PostAsJsonAsync($"{Admin}/reports/{id}/resolve", new { action = "suspend", note = "Cheating confirmed" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        var admin = await AdminWith("squad_up:view", "squad_up:manage", "users:ban");
        (await admin.PostAsJsonAsync($"{Admin}/reports/{id}/resolve", new { action = "suspend", note = "Cheating confirmed" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Scalar<bool>("SELECT is_suspended FROM public.profiles WHERE id = @id", new { id = reported.Id })).Should().BeTrue();
        await Supabase.Received().LogoutUserAsync(reported.Id.ToString(), Arg.Any<CancellationToken>());
        (await Scalar<string>("SELECT resolution FROM public.squad_up_reports WHERE id = @id", new { id })).Should().Be("suspended");
    }

    [Fact]
    public async Task ExistingAdminSuspendEndpoint_StillSuspends_AfterTheLogicMovedToAService()
    {
        var target = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(target);
        var admin = await AdminWith("users:ban");
        var response = await admin.PostAsJsonAsync($"/api/admin/users/{target}/suspend", new { reason = "Test suspension" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Scalar<string>("SELECT suspension_reason FROM public.profiles WHERE id = @target", new { target })).Should().Be("Test suspension");
        await Supabase.Received().LogoutUserAsync(target.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Overview_CountsReportsByStatusAndReason()
    {
        var viewer = await AdminWith("squad_up:view");
        var before = (await Json(await viewer.GetAsync($"{Admin}/overview"))).GetProperty("reports").GetProperty("open").GetInt32();
        await OpenReport("fake_rank");
        var after = (await Json(await viewer.GetAsync($"{Admin}/overview"))).GetProperty("reports");
        after.GetProperty("open").GetInt32().Should().Be(before + 1);
        after.GetProperty("byReason").EnumerateArray().Should().Contain(r => r.GetProperty("reason").GetString() == "fake_rank");
    }
}
