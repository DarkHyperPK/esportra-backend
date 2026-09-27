using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Tournament;

// ── GET /api/tournament-templates ────────────────────────────────────────────

[Collection(IntegrationCollection.Name)]
public sealed class TournamentTemplateListTests : IAsyncLifetime
{
    private readonly ApiFactory _factory;

    // Unique slug per test session — avoids conflicts on repeated runs
    private static readonly string TestSlug = $"tmpl-{Guid.NewGuid():N}"[..20];

    public TournamentTemplateListTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        // Warm up the host so ImportPackagedCatalogAsync runs before we seed
        _ = _factory.CreateClient();
        await SeedTemplateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetTemplates_ReturnsOk_WithTemplateList()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/tournament-templates");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array, "endpoint must return a JSON array of templates");
    }

    [Fact]
    public async Task GetTemplates_RequiresNoAuth_ReturnsOkForAnonymous()
    {
        var client = _factory.CreateClient(); // no auth header

        var response = await client.GetAsync("/api/tournament-templates");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "tournament template list is a public endpoint and must not require authentication");
    }

    [Fact]
    public async Task GetTemplates_ReturnsCacheControlHeader()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/tournament-templates");

        response.Headers.CacheControl.Should().NotBeNull("Cache-Control header must be present");
        response.Headers.CacheControl!.Public.Should().BeTrue("Cache-Control must be public");
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(600),
            "max-age must be 600 seconds");
    }

    [Fact]
    public async Task GetTemplates_ReturnsETagHeader()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/tournament-templates");

        response.Headers.ETag.Should().NotBeNull("ETag header must be present");
        response.Headers.ETag!.Tag.Should().NotBeNullOrEmpty("ETag value must not be empty");
    }

    [Fact]
    public async Task GetTemplates_Returns304_WhenETagMatches()
    {
        var client = _factory.CreateClient();

        // First request — obtain the ETag
        var first = await client.GetAsync("/api/tournament-templates");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = first.Headers.GetValues("ETag").First();

        // Second request — send the ETag back via If-None-Match
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/tournament-templates");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await client.SendAsync(request);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified,
            "server must return 304 when the client has a fresh copy matching the current ETag");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedTemplateAsync()
    {
        await using var conn = _factory.Seeder.OpenConnection();

        var versionId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM public.game_catalog_versions WHERE is_active = true AND status = 'active' LIMIT 1")
            ?? throw new InvalidOperationException(
                "No active game catalog version found. Ensure the host is warmed up before seeding.");

        var gameSlug = $"tg-{TestSlug}";
        var gameId = await conn.QuerySingleAsync<Guid>("""
            INSERT INTO public.game_catalog_games (version_id, slug, name, game_type, default_mode_key)
            VALUES (@versionId, @gameSlug, 'Test Template Game', 'bracket', 'default')
            ON CONFLICT (version_id, slug) DO UPDATE SET name = EXCLUDED.name
            RETURNING id
            """, new { versionId, gameSlug });

        await conn.ExecuteAsync("""
            INSERT INTO public.tournament_templates
                (game_catalog_id, slug, rules_text, rules_updated_at, default_best_of,
                 default_max_teams, recommended_team_counts, is_active, sort_order)
            VALUES (@gameId, @slug, 'Seeded test rules.', now(), 3, 16, ARRAY[8, 16], true, 99)
            ON CONFLICT (slug) DO UPDATE SET is_active = true
            """, new { gameId, slug = TestSlug });
    }
}

// ── GET /api/tournament-templates/{slug} ─────────────────────────────────────

[Collection(IntegrationCollection.Name)]
public sealed class TournamentTemplateBySlugTests : IAsyncLifetime
{
    private readonly ApiFactory _factory;

    private static readonly string TestSlug = $"tmpl-{Guid.NewGuid():N}"[..20];

    public TournamentTemplateBySlugTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        _ = _factory.CreateClient();
        await SeedTemplateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetTemplateBySlug_ReturnsOk_ForValidSlug()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/tournament-templates/{TestSlug}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("slug").GetString().Should().Be(TestSlug);
    }

    [Fact]
    public async Task GetTemplateBySlug_Returns404_ForInvalidSlug()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/tournament-templates/this-slug-does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a slug that does not exist in tournament_templates must return 404");
    }

    [Fact]
    public async Task GetTemplateBySlug_RequiresNoAuth_ReturnsOkForAnonymous()
    {
        var client = _factory.CreateClient(); // no auth header

        var response = await client.GetAsync($"/api/tournament-templates/{TestSlug}");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "single template lookup is a public endpoint and must not require authentication");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedTemplateAsync()
    {
        await using var conn = _factory.Seeder.OpenConnection();

        var versionId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM public.game_catalog_versions WHERE is_active = true AND status = 'active' LIMIT 1")
            ?? throw new InvalidOperationException(
                "No active game catalog version found. Ensure the host is warmed up before seeding.");

        var gameSlug = $"tg-{TestSlug}";
        var gameId = await conn.QuerySingleAsync<Guid>("""
            INSERT INTO public.game_catalog_games (version_id, slug, name, game_type, default_mode_key)
            VALUES (@versionId, @gameSlug, 'Test Game For Slug Tests', 'bracket', 'default')
            ON CONFLICT (version_id, slug) DO UPDATE SET name = EXCLUDED.name
            RETURNING id
            """, new { versionId, gameSlug });

        await conn.ExecuteAsync("""
            INSERT INTO public.tournament_templates
                (game_catalog_id, slug, rules_text, rules_updated_at, default_best_of,
                 default_max_teams, recommended_team_counts, is_active, sort_order)
            VALUES (@gameId, @slug, 'Slug test rules.', now(), 3, 16, ARRAY[8, 16], true, 99)
            ON CONFLICT (slug) DO UPDATE SET is_active = true
            """, new { gameId, slug = TestSlug });
    }
}

// ── POST /api/tournaments — templateId + venueAddress fields ─────────────────

[Collection(IntegrationCollection.Name)]
public sealed class TournamentCreateTemplateFieldsTests : IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    private static readonly Guid OrganizerA = Guid.NewGuid();
    private static readonly Guid OrganizerB = Guid.NewGuid();
    private static readonly Guid OrganizerC = Guid.NewGuid();
    private static readonly Guid OrganizerD = Guid.NewGuid();

    public TournamentCreateTemplateFieldsTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    public async Task InitializeAsync()
    {
        // Warm up so the packaged game catalog (valorant, etc.) is available
        _ = _factory.CreateClient();
        await SeedVerifiedOrganizerAsync(OrganizerA);
        await SeedVerifiedOrganizerAsync(OrganizerB);
        await SeedVerifiedOrganizerAsync(OrganizerC);
        await SeedVerifiedOrganizerAsync(OrganizerD);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── templateId stored ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTournament_WithTemplateId_StoresTemplateId()
    {
        var templateId = Guid.NewGuid();
        var client = _factory.CreateAuthenticatedClient(OrganizerA);

        var response = await client.PostAsJsonAsync("/api/tournaments", BuildRequest(
            name: "TemplateId Test",
            templateId: templateId));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tournamentId = body.GetProperty("id").GetGuid();

        await using var conn = _seeder.OpenConnection();
        var stored = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT template_id FROM public.tournaments WHERE id = @tournamentId",
            new { tournamentId });

        stored.Should().Be(templateId, "template_id must be persisted to the tournaments row");
    }

    // ── venueAddress stored ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateTournament_WithVenueAddress_StoresVenueAddress()
    {
        const string address = "123 Main St, Test City, TX 12345";
        var client = _factory.CreateAuthenticatedClient(OrganizerB);

        var response = await client.PostAsJsonAsync("/api/tournaments", BuildRequest(
            name: "VenueAddress Test",
            venueAddress: address));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tournamentId = body.GetProperty("id").GetGuid();

        await using var conn = _seeder.OpenConnection();
        var stored = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT venue_address FROM public.tournaments WHERE id = @tournamentId",
            new { tournamentId });

        stored.Should().Be(address, "venue_address must be persisted to the tournaments row");
    }

    // ── HTML in venueAddress is rejected ─────────────────────────────────────

    [Fact]
    public async Task CreateTournament_WithHtmlInVenueAddress_ReturnsBadRequest()
    {
        var client = _factory.CreateAuthenticatedClient(OrganizerC);

        var response = await client.PostAsJsonAsync("/api/tournaments", BuildRequest(
            name: "XSS Test",
            venueAddress: "<script>alert('xss')</script>"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "venue addresses containing HTML tags must be rejected");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should()
            .Contain("HTML", "error message must identify HTML as the reason for rejection");
    }

    // ── Creation without templateId is unaffected ─────────────────────────────

    [Fact]
    public async Task CreateTournament_WithoutTemplateId_StillSucceeds()
    {
        var client = _factory.CreateAuthenticatedClient(OrganizerD);

        var response = await client.PostAsJsonAsync("/api/tournaments", BuildRequest(
            name: "No Template Test"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "tournament creation without a templateId must still succeed — no regression introduced");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object BuildRequest(
        string name,
        Guid? templateId = null,
        string? venueAddress = null) => new
        {
            name,
            game = "valorant",
            gameMode = "5v5",
            maxTeams = 8,
            startDate = DateTime.UtcNow.AddDays(30),
            templateId,
            venueAddress,
        };

    private async Task SeedVerifiedOrganizerAsync(Guid userId)
    {
        await _seeder.SeedAuthUserAsync(userId);
        await _seeder.SeedUserRoleAsync(userId, "organizer");

        await using var conn = _seeder.OpenConnection();

        // verified_roles has no composite UNIQUE constraint, use WHERE NOT EXISTS guard
        await conn.ExecuteAsync("""
            INSERT INTO public.verified_roles (user_id, role, status, is_active)
            SELECT @userId, 'organizer', 'approved', true
            WHERE NOT EXISTS (
                SELECT 1 FROM public.verified_roles
                WHERE user_id = @userId AND role = 'organizer'
            )
            """, new { userId });

        // organizations — one org per owner (owner_id NOT NULL, typically unique in practice)
        await conn.ExecuteAsync("""
            INSERT INTO public.organizations (owner_id, name, slug, created_at, updated_at)
            SELECT @userId, @name, @slug, NOW(), NOW()
            WHERE NOT EXISTS (
                SELECT 1 FROM public.organizations WHERE owner_id = @userId
            )
            """, new
        {
            userId,
            name = $"Org {userId:N}"[..20],
            slug = $"org-{userId:N}"[..20],
        });
    }
}
