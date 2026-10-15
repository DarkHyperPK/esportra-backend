using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

/// <summary>
/// Integration tests for <see cref="Esportra.Api.Endpoints.BroadcastSponsorProofEndpoints"/>
/// (PROJ-059 P4 T-BE-T36).
///
/// Covers:
///   POST /api/broadcast/shows/{showId}/sponsor-impressions:
///   - Node without show ownership → 403
///   - Batch > 50 → 400
///   - Invalid slotType → 400
///   - Valid batch → rows inserted, response { accepted: N }
///
///   GET /api/broadcast/shows/{showId}/sponsor-proof:
///   - Non-org-owner → 403
///   - Org owner, no impressions → { sponsors: [] }
///   - Org owner, mixed closed/open impressions → correct totalOnAirSeconds (open uses show.ended_at)
///   - hasIncomplete = true when open impressions exist
///   - placementCount counts only closed impressions
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed partial class BroadcastSponsorProofEndpointsTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    public BroadcastSponsorProofEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private HttpClient CreateNodeClient(string rawToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {rawToken}");
        return client;
    }

    private HttpClient CreateUserClient(Guid userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestAuthHelper.BearerHeader(userId));
        return client;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// Seeds: auth user → organization → broadcast_show → broadcast_node → broadcast_show_sponsors row.
    /// Returns (rawToken, ownerId, orgId, showId, nodeId, sponsorId).
    /// showEndedAt is seeded only when provided.
    /// </summary>
    private async Task<(string RawToken, Guid OwnerId, Guid OrgId, Guid ShowId, Guid NodeId, Guid SponsorId)>
        SeedFullContextAsync(DateTimeOffset? showEndedAt = null)
    {
        var ownerId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(ownerId);
        await _seeder.SeedUserRoleAsync(ownerId, "organizer");

        await using var conn = _seeder.OpenConnection();

        var orgId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO public.organizations (id, owner_id, name, slug, created_at, updated_at)
            VALUES (@id, @ownerId, @name, @slug, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = orgId, ownerId, name = $"Org {orgId:N}"[..20], slug = $"org-{orgId:N}"[..20] });

        var showId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO public.broadcast_shows
                (id, org_id, title, status, ended_at, created_by, created_at, updated_at)
            VALUES
                (@id, @orgId, 'Test Show', 'ended', @endedAt, @ownerId, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = showId, orgId, ownerId, endedAt = showEndedAt });

        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashToken(rawToken);
        var nodeId = await conn.QuerySingleAsync<Guid>("""
            INSERT INTO public.broadcast_nodes (org_id, name, token_hash, capabilities, is_active)
            VALUES (@orgId, 'test-node', @tokenHash, '{}'::jsonb, true)
            RETURNING id
            """,
            new { orgId, tokenHash });

        var sponsorId = await conn.QuerySingleAsync<Guid>("""
            INSERT INTO public.broadcast_show_sponsors
                (show_id, name, logo_url, display_slot, display_order, is_active)
            VALUES (@showId, 'Acme Corp', null, 'lower_third', 1, true)
            RETURNING id
            """,
            new { showId });

        return (rawToken, ownerId, orgId, showId, nodeId, sponsorId);
    }

    // ── POST: ownership guard ─────────────────────────────────────────────────

    [Fact]
    public async Task PostImpressions_Returns401_WhenNoAuthorizationHeader()
    {
        var client = _factory.CreateClient();
        var showId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(
            $"/api/broadcast/shows/{showId}/sponsor-impressions",
            new { impressions = Array.Empty<object>() });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostImpressions_Returns403_WhenNodeDoesNotOwnShow()
    {
        // Seed node A owning org A
        var (rawToken, _, _, _, _, _) = await SeedFullContextAsync();
        // Seed an independent show under a different org (no node owns it)
        var otherOwnerId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(otherOwnerId);
        await using var conn = _seeder.OpenConnection();
        var otherOrgId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO public.organizations (id, owner_id, name, slug, created_at, updated_at)
            VALUES (@id, @ownerId, @name, @slug, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = otherOrgId, ownerId = otherOwnerId, name = $"OtherOrg {otherOrgId:N}"[..20], slug = $"oo-{otherOrgId:N}"[..20] });
        var otherShowId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO public.broadcast_shows
                (id, org_id, title, status, created_by, created_at, updated_at)
            VALUES (@id, @orgId, 'Other Show', 'live', @ownerId, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = otherShowId, orgId = otherOrgId, ownerId = otherOwnerId });

        var client = CreateNodeClient(rawToken);
        var response = await client.PostAsJsonAsync(
            $"/api/broadcast/shows/{otherShowId}/sponsor-impressions",
            new { impressions = Array.Empty<object>() });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── POST: batch validation ────────────────────────────────────────────────

    [Fact]
    public async Task PostImpressions_Returns400_WhenBatchExceeds50()
    {
        var (rawToken, _, _, showId, _, sponsorId) = await SeedFullContextAsync();
        var client = CreateNodeClient(rawToken);

        var impressions = Enumerable.Range(0, 51).Select(_ => new
        {
            sponsorId,
            startedAt = DateTimeOffset.UtcNow,
            slotType = "lower_third",
        }).ToList();

        var response = await client.PostAsJsonAsync(
            $"/api/broadcast/shows/{showId}/sponsor-impressions",
            new { impressions });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostImpressions_Returns400_WhenSlotTypeIsInvalid()
    {
        var (rawToken, _, _, showId, _, sponsorId) = await SeedFullContextAsync();
        var client = CreateNodeClient(rawToken);

        var response = await client.PostAsJsonAsync(
            $"/api/broadcast/shows/{showId}/sponsor-impressions",
            new
            {
                impressions = new[]
                {
                    new { sponsorId, startedAt = DateTimeOffset.UtcNow, slotType = "INVALID_TYPE" },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── POST: valid batch insert ──────────────────────────────────────────────

    [Fact]
    public async Task PostImpressions_InsertsRows_WhenBatchIsValid()
    {
        var (rawToken, _, _, showId, _, sponsorId) = await SeedFullContextAsync();
        var client = CreateNodeClient(rawToken);

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var endedAt = DateTimeOffset.UtcNow.AddMinutes(-7);

        var response = await client.PostAsJsonAsync(
            $"/api/broadcast/shows/{showId}/sponsor-impressions",
            new
            {
                impressions = new object[]
                {
                    new { sponsorId, startedAt, endedAt = (DateTimeOffset?)endedAt, slotType = "lower_third" },
                    new { sponsorId, startedAt = DateTimeOffset.UtcNow.AddMinutes(-5), endedAt = (DateTimeOffset?)null, slotType = "scorebug" },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("accepted").GetInt32().Should().Be(2);

        // Verify rows were written
        await using var conn = _seeder.OpenConnection();
        var count = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM broadcast_sponsor_impressions WHERE show_id = @showId",
            new { showId });
        count.Should().Be(2);
    }

    // ── GET: ownership guard ──────────────────────────────────────────────────

    [Fact]
    public async Task GetSponsorProof_Returns403_WhenUserIsNotOrgOwner()
    {
        var (_, ownerId, _, showId, _, _) = await SeedFullContextAsync();

        // Different user — not the org owner
        var strangerUserId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(strangerUserId);
        var client = CreateUserClient(strangerUserId);

        var response = await client.GetAsync($"/api/broadcast/shows/{showId}/sponsor-proof");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── GET: empty show ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetSponsorProof_ReturnsEmptySponsors_WhenNoImpressionsExist()
    {
        var (_, ownerId, _, showId, _, _) = await SeedFullContextAsync();
        var client = CreateUserClient(ownerId);

        var response = await client.GetAsync($"/api/broadcast/shows/{showId}/sponsor-proof");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("sponsors").GetArrayLength().Should().Be(0);
    }

    // ── GET: mixed closed/open impressions ───────────────────────────────────

    [Fact]
    public async Task GetSponsorProof_CalculatesCorrectOnAirSeconds_WhenOpenImpressionFallsBackToShowEndedAt()
    {
        // Show ended at T+60s from base; closed impression ran from 0→30s; open ran from 40s→show_end (60s)
        var showEndedAt = DateTimeOffset.UtcNow;
        var (_, ownerId, _, showId, _, sponsorId) = await SeedFullContextAsync(showEndedAt);

        var baseTime = showEndedAt.AddSeconds(-60);

        await using var conn = _seeder.OpenConnection();

        // Closed impression: 30s on air → placement_count = 1
        await conn.ExecuteAsync("""
            INSERT INTO broadcast_sponsor_impressions
                (show_id, sponsor_id, started_at, ended_at, slot_type)
            VALUES (@showId, @sponsorId, @startedAt, @endedAt, 'lower_third')
            """,
            new
            {
                showId,
                sponsorId,
                startedAt = baseTime,
                endedAt = baseTime.AddSeconds(30),
            });

        // Open impression: started_at = T-20s; COALESCE(null, show.ended_at) = showEndedAt → 20s on air
        await conn.ExecuteAsync("""
            INSERT INTO broadcast_sponsor_impressions
                (show_id, sponsor_id, started_at, ended_at, slot_type)
            VALUES (@showId, @sponsorId, @startedAt, NULL, 'lower_third')
            """,
            new
            {
                showId,
                sponsorId,
                startedAt = showEndedAt.AddSeconds(-20),
            });

        var client = CreateUserClient(ownerId);
        var response = await client.GetAsync($"/api/broadcast/shows/{showId}/sponsor-proof");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var sponsors = body.GetProperty("sponsors");
        sponsors.GetArrayLength().Should().Be(1);

        var sponsor = sponsors[0];
        // closed 30s + open 20s = 50s total on air
        sponsor.GetProperty("totalOnAirSeconds").GetDouble().Should().BeApproximately(50.0, precision: 0.5);
        // placementCount counts only closed impressions (AC9)
        sponsor.GetProperty("placementCount").GetInt64().Should().Be(1);
        // hasIncomplete = true because open impression exists (AC11)
        sponsor.GetProperty("hasIncomplete").GetBoolean().Should().BeTrue();
        sponsor.GetProperty("incompleteCount").GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task GetSponsorProof_ReturnsHasIncompleteFalse_WhenAllImpressionsClosed()
    {
        var showEndedAt = DateTimeOffset.UtcNow;
        var (_, ownerId, _, showId, _, sponsorId) = await SeedFullContextAsync(showEndedAt);

        var baseTime = showEndedAt.AddSeconds(-60);
        await using var conn = _seeder.OpenConnection();

        await conn.ExecuteAsync("""
            INSERT INTO broadcast_sponsor_impressions
                (show_id, sponsor_id, started_at, ended_at, slot_type)
            VALUES (@showId, @sponsorId, @startedAt, @endedAt, 'scorebug')
            """,
            new
            {
                showId,
                sponsorId,
                startedAt = baseTime,
                endedAt = baseTime.AddSeconds(45),
            });

        var client = CreateUserClient(ownerId);
        var response = await client.GetAsync($"/api/broadcast/shows/{showId}/sponsor-proof");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sponsor = body.GetProperty("sponsors")[0];

        sponsor.GetProperty("hasIncomplete").GetBoolean().Should().BeFalse();
        sponsor.GetProperty("incompleteCount").GetInt64().Should().Be(0);
        sponsor.GetProperty("placementCount").GetInt64().Should().Be(1);
        sponsor.GetProperty("totalOnAirSeconds").GetDouble().Should().BeApproximately(45.0, precision: 0.5);
    }

    [Fact]
    public async Task GetSponsorProof_ReturnsAllRequiredFields_WhenImpressionsExist()
    {
        var showEndedAt = DateTimeOffset.UtcNow;
        var (_, ownerId, _, showId, _, sponsorId) = await SeedFullContextAsync(showEndedAt);

        await using var conn = _seeder.OpenConnection();

        await conn.ExecuteAsync("""
            INSERT INTO broadcast_sponsor_impressions
                (show_id, sponsor_id, started_at, ended_at, slot_type)
            VALUES (@showId, @sponsorId, @startedAt, @endedAt, 'lower_third')
            """,
            new
            {
                showId,
                sponsorId,
                startedAt = showEndedAt.AddSeconds(-30),
                endedAt = showEndedAt,
            });

        var client = CreateUserClient(ownerId);
        var response = await client.GetAsync($"/api/broadcast/shows/{showId}/sponsor-proof");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Top-level fields
        body.TryGetProperty("showId", out _).Should().BeTrue();
        body.TryGetProperty("showTitle", out _).Should().BeTrue();
        body.TryGetProperty("generatedAt", out _).Should().BeTrue();

        var sponsor = body.GetProperty("sponsors")[0];
        // AC10: response contains all required fields
        sponsor.TryGetProperty("sponsorId", out _).Should().BeTrue();
        sponsor.TryGetProperty("name", out _).Should().BeTrue();
        sponsor.TryGetProperty("totalOnAirSeconds", out _).Should().BeTrue();
        sponsor.TryGetProperty("placementCount", out _).Should().BeTrue();
        sponsor.TryGetProperty("bySlotType", out _).Should().BeTrue();
        sponsor.TryGetProperty("hasIncomplete", out _).Should().BeTrue();
        sponsor.TryGetProperty("incompleteCount", out _).Should().BeTrue();

        // bySlotType breakdown must contain the slot used
        var bySlotType = sponsor.GetProperty("bySlotType");
        bySlotType.TryGetProperty("lower_third", out var slotEntry).Should().BeTrue();
        slotEntry.GetProperty("count").GetInt64().Should().Be(1);
        slotEntry.GetProperty("totalSeconds").GetDouble().Should().BeApproximately(30.0, precision: 0.5);
    }
}
