using System.Net;
using System.Net.Http.Json;
using Dapper;
using Esportra.Api.Endpoints;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.DeveloperApi;

public sealed class DeveloperAccessRequestEndpointTests
{
    // ── ValidateIntendedUse ───────────────────────────────────────────────────

    [Fact]
    public void ValidateIntendedUse_ReturnsError_WhenNull()
    {
        var result = DeveloperAccessRequestEndpoints.ValidateIntendedUse(null);
        result.Should().Be("intended_use is required");
    }

    [Fact]
    public void ValidateIntendedUse_ReturnsError_WhenWhitespace()
    {
        var result = DeveloperAccessRequestEndpoints.ValidateIntendedUse("   ");
        result.Should().Be("intended_use is required");
    }

    [Fact]
    public void ValidateIntendedUse_ReturnsError_WhenTooShort()
    {
        var result = DeveloperAccessRequestEndpoints.ValidateIntendedUse("short");
        result.Should().Be("intended_use must be at least 50 characters");
    }

    [Fact]
    public void ValidateIntendedUse_ReturnsError_WhenBoundaryShort()
    {
        // 49 characters — just under minimum
        var input = new string('a', 49);
        var result = DeveloperAccessRequestEndpoints.ValidateIntendedUse(input);
        result.Should().Be("intended_use must be at least 50 characters");
    }

    [Fact]
    public void ValidateIntendedUse_ReturnsNull_WhenExactlyMinLength()
    {
        var input = new string('a', 50);
        var result = DeveloperAccessRequestEndpoints.ValidateIntendedUse(input);
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateIntendedUse_ReturnsNull_WhenExactlyMaxLength()
    {
        var input = new string('a', 2000);
        var result = DeveloperAccessRequestEndpoints.ValidateIntendedUse(input);
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateIntendedUse_ReturnsError_WhenTooLong()
    {
        var input = new string('a', 2001);
        var result = DeveloperAccessRequestEndpoints.ValidateIntendedUse(input);
        result.Should().Be("intended_use must not exceed 2000 characters");
    }

    [Fact]
    public void ValidateIntendedUse_ReturnsNull_WhenValid()
    {
        var input = "We plan to build a tournament bracket aggregation service for competitive CS players.";
        var result = DeveloperAccessRequestEndpoints.ValidateIntendedUse(input);
        result.Should().BeNull();
    }

    // ── NormalizeStatus ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("pending", "pending")]
    [InlineData("approved", "approved")]
    [InlineData("rejected", "rejected")]
    [InlineData("PENDING", "pending")]
    [InlineData("Approved", "approved")]
    [InlineData("REJECTED", "rejected")]
    public void NormalizeStatus_ReturnsLowercaseStatus_WhenValid(string input, string expected)
    {
        var result = DeveloperAccessRequestEndpoints.NormalizeStatus(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("all")]
    [InlineData("active")]
    public void NormalizeStatus_ReturnsNull_WhenInvalidOrMissing(string? input)
    {
        var result = DeveloperAccessRequestEndpoints.NormalizeStatus(input);
        result.Should().BeNull();
    }
}

// ── Integration tests ─────────────────────────────────────────────────────────

[Collection(IntegrationCollection.Name)]
public sealed class DeveloperAccessRequestIntegrationTests(ApiFactory factory)
{
    private readonly ApiFactory _factory = factory;
    private readonly DbSeeder _seeder = factory.Seeder;

    // Shared user IDs — seeded idempotently via SeedUsersAsync()
    private static readonly Guid OwnerUserId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();
    private static readonly Guid AdminUserId = Guid.NewGuid();

    private const string ValidIntendedUse =
        "We plan to build a tournament bracket aggregation service for competitive esports players.";

    // ── POST /api/developer/access-requests ──────────────────────────────────

    [Fact]
    public async Task SubmitAccessRequest_HappyPath_Returns201()
    {
        await SeedUsersAsync();
        var orgId = await SeedOrgAsync(OwnerUserId);
        var client = _factory.CreateAuthenticatedClient(OwnerUserId);

        var response = await client.PostAsJsonAsync("/api/developer/access-requests", new
        {
            organization_id = orgId.ToString(),
            intended_use = ValidIntendedUse,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("status").GetString().Should().Be("pending");
        body.TryGetProperty("id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task SubmitAccessRequest_DuplicatePending_Returns409()
    {
        await SeedUsersAsync();
        var orgId = await SeedOrgAsync(OwnerUserId);
        await SeedAccessRequestAsync(orgId, OwnerUserId, "pending");
        var client = _factory.CreateAuthenticatedClient(OwnerUserId);

        var response = await client.PostAsJsonAsync("/api/developer/access-requests", new
        {
            organization_id = orgId.ToString(),
            intended_use = ValidIntendedUse,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SubmitAccessRequest_AlreadyApproved_Returns422()
    {
        await SeedUsersAsync();
        var orgId = await SeedOrgAsync(OwnerUserId, isApiApproved: true);
        var client = _factory.CreateAuthenticatedClient(OwnerUserId);

        var response = await client.PostAsJsonAsync("/api/developer/access-requests", new
        {
            organization_id = orgId.ToString(),
            intended_use = ValidIntendedUse,
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task SubmitAccessRequest_RateLimit_Returns429()
    {
        await SeedUsersAsync();
        var orgId = await SeedOrgAsync(OwnerUserId);
        // A recently rejected request triggers the 24-hour rate limit
        await SeedAccessRequestAsync(orgId, OwnerUserId, "rejected");
        var client = _factory.CreateAuthenticatedClient(OwnerUserId);

        var response = await client.PostAsJsonAsync("/api/developer/access-requests", new
        {
            organization_id = orgId.ToString(),
            intended_use = ValidIntendedUse,
        });

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task SubmitAccessRequest_MissingOrgOwnership_Returns403()
    {
        await SeedUsersAsync();
        var orgId = await SeedOrgAsync(OtherUserId);
        var client = _factory.CreateAuthenticatedClient(OwnerUserId);

        var response = await client.PostAsJsonAsync("/api/developer/access-requests", new
        {
            organization_id = orgId.ToString(),
            intended_use = ValidIntendedUse,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── GET /api/developer/access-requests ───────────────────────────────────

    [Fact]
    public async Task GetAccessRequest_WithExistingRequest_Returns200()
    {
        await SeedUsersAsync();
        var orgId = await SeedOrgAsync(OwnerUserId);
        await SeedAccessRequestAsync(orgId, OwnerUserId, "pending");
        var client = _factory.CreateAuthenticatedClient(OwnerUserId);

        var response = await client.GetAsync($"/api/developer/access-requests?organizationId={orgId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("status").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task GetAccessRequest_WhenNone_Returns404()
    {
        await SeedUsersAsync();
        var orgId = await SeedOrgAsync(OwnerUserId);
        var client = _factory.CreateAuthenticatedClient(OwnerUserId);

        var response = await client.GetAsync($"/api/developer/access-requests?organizationId={orgId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /api/admin/developer-access-requests ─────────────────────────────

    [Fact]
    public async Task AdminListAccessRequests_ReturnsResults_Returns200()
    {
        await SeedUsersAsync();
        var orgId = await SeedOrgAsync(OwnerUserId);
        await SeedAccessRequestAsync(orgId, OwnerUserId, "pending");
        var client = _factory.CreateAuthenticatedClient(AdminUserId);

        var response = await client.GetAsync("/api/admin/developer-access-requests?page=1&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.TryGetProperty("items", out _).Should().BeTrue();
        body.TryGetProperty("total", out _).Should().BeTrue();
    }

    [Fact]
    public async Task AdminListAccessRequests_WithStatusFilter_Returns200()
    {
        await SeedUsersAsync();
        var client = _factory.CreateAuthenticatedClient(AdminUserId);

        var response = await client.GetAsync("/api/admin/developer-access-requests?page=1&pageSize=10&status=pending");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.TryGetProperty("items", out _).Should().BeTrue();
    }

    [Fact]
    public async Task AdminListAccessRequests_NonAdmin_Returns403()
    {
        await SeedUsersAsync();
        var client = _factory.CreateAuthenticatedClient(OwnerUserId);

        var response = await client.GetAsync("/api/admin/developer-access-requests?page=1&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── PATCH /api/admin/developer-access-requests/{id} ──────────────────────

    [Fact]
    public async Task AdminReviewAccessRequest_Approve_Returns200()
    {
        await SeedUsersAsync();
        var orgId = await SeedOrgAsync(OwnerUserId);
        var requestId = await SeedAccessRequestAsync(orgId, OwnerUserId, "pending");
        var client = _factory.CreateAuthenticatedClient(AdminUserId);

        var response = await client.PatchAsJsonAsync(
            $"/api/admin/developer-access-requests/{requestId}",
            new { action = "approve", admin_notes = "Looks good." });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("status").GetString().Should().Be("approved");
    }

    [Fact]
    public async Task AdminReviewAccessRequest_Reject_Returns200()
    {
        await SeedUsersAsync();
        var orgId = await SeedOrgAsync(OwnerUserId);
        var requestId = await SeedAccessRequestAsync(orgId, OwnerUserId, "pending");
        var client = _factory.CreateAuthenticatedClient(AdminUserId);

        var response = await client.PatchAsJsonAsync(
            $"/api/admin/developer-access-requests/{requestId}",
            new { action = "reject", admin_notes = "Insufficient detail." });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("status").GetString().Should().Be("rejected");
    }

    [Fact]
    public async Task AdminReviewAccessRequest_InvalidAction_Returns400()
    {
        await SeedUsersAsync();
        var client = _factory.CreateAuthenticatedClient(AdminUserId);

        var response = await client.PatchAsJsonAsync(
            $"/api/admin/developer-access-requests/{Guid.NewGuid()}",
            new { action = "invalid_action" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AdminReviewAccessRequest_AdminNotesTooLong_Returns400()
    {
        await SeedUsersAsync();
        var client = _factory.CreateAuthenticatedClient(AdminUserId);

        var response = await client.PatchAsJsonAsync(
            $"/api/admin/developer-access-requests/{Guid.NewGuid()}",
            new { action = "approve", admin_notes = new string('x', 5001) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── PATCH /api/developer/keys/{keyId} ────────────────────────────────────

    [Fact]
    public async Task RenameKey_Success_Returns200()
    {
        await SeedUsersAsync();
        var orgId = await SeedOrgAsync(OwnerUserId);
        var keyId = await SeedApiKeyAsync(orgId, OwnerUserId);
        var client = _factory.CreateAuthenticatedClient(OwnerUserId);

        var response = await client.PatchAsJsonAsync(
            $"/api/developer/keys/{keyId}",
            new { name = "Renamed Key" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("name").GetString().Should().Be("Renamed Key");
    }

    [Fact]
    public async Task RenameKey_NameTooLong_Returns400()
    {
        await SeedUsersAsync();
        var orgId = await SeedOrgAsync(OwnerUserId);
        var keyId = await SeedApiKeyAsync(orgId, OwnerUserId);
        var client = _factory.CreateAuthenticatedClient(OwnerUserId);

        var response = await client.PatchAsJsonAsync(
            $"/api/developer/keys/{keyId}",
            new { name = new string('a', 101) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RenameKey_NotOrgOwner_Returns403()
    {
        await SeedUsersAsync();
        var orgId = await SeedOrgAsync(OtherUserId);
        var keyId = await SeedApiKeyAsync(orgId, OtherUserId);
        // OwnerUserId does not own this org
        var client = _factory.CreateAuthenticatedClient(OwnerUserId);

        var response = await client.PatchAsJsonAsync(
            $"/api/developer/keys/{keyId}",
            new { name = "Should Fail" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Seeding helpers ───────────────────────────────────────────────────────

    private async Task SeedUsersAsync()
    {
        await _seeder.SeedAuthUserAsync(OwnerUserId);
        await _seeder.SeedUserRoleAsync(OwnerUserId, "casual");
        await _seeder.SeedAuthUserAsync(OtherUserId);
        await _seeder.SeedUserRoleAsync(OtherUserId, "casual");
        await _seeder.SeedAuthUserAsync(AdminUserId);
        await _seeder.SeedAdminPanelRoleAsync(AdminUserId);
    }

    private async Task<Guid> SeedOrgAsync(Guid ownerId, bool isApiApproved = false)
    {
        var orgId = Guid.NewGuid();
        await using var conn = _seeder.OpenConnection();
        var actualId = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.organizations (id, owner_id, name, slug, description, logo_url, banner_url, social_links)
            VALUES (@id, @ownerId, @name, @slug, '', '', '', '{}'::jsonb)
            ON CONFLICT (owner_id) DO UPDATE SET name = EXCLUDED.name, slug = EXCLUDED.slug
            RETURNING id
            """,
            new { id = orgId, ownerId, name = $"Test Org {orgId:N}"[..30], slug = $"org-{orgId:N}"[..20] });

        if (isApiApproved)
        {
            await conn.ExecuteAsync(
                "UPDATE public.organizations SET is_api_approved = true WHERE id = @id",
                new { id = actualId });
        }

        return actualId;
    }

    private async Task<Guid> SeedAccessRequestAsync(Guid orgId, Guid requestedBy, string status = "pending")
    {
        var id = Guid.NewGuid();
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO public.developer_access_requests (id, organization_id, requested_by, intended_use, status, created_at)
            VALUES (@id, @orgId, @requestedBy, @intendedUse, @status, NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id, orgId, requestedBy, intendedUse = ValidIntendedUse, status });
        return id;
    }

    private async Task<Guid> SeedApiKeyAsync(Guid orgId, Guid createdBy)
    {
        var keyId = Guid.NewGuid();
        var keyHash = $"testhash{keyId:N}";
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO public.developer_api_keys (id, organization_id, name, key_hash, key_prefix, environment, status, scopes, rate_limit_per_min, created_by)
            VALUES (@keyId, @orgId, 'Test Key', @keyHash, 'ek_sand_test0000', 'sandbox', 'active', '{}', 60, @createdBy)
            ON CONFLICT (id) DO NOTHING
            """,
            new { keyId, orgId, keyHash, createdBy });
        return keyId;
    }
}
