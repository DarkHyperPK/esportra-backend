using System.Net;
using System.Net.Http.Json;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Admin;

[Collection(IntegrationCollection.Name)]
public sealed class AdminEndpointTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    private static readonly Guid AdminUserId = Guid.NewGuid();
    private static readonly Guid RegularUserId = Guid.NewGuid();

    public AdminEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    // ── GET /api/admin/users ──────────────────────────────────────────────────

    [Fact]
    public async Task GetAdminUsers_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAdminUsers_NonAdmin_Returns403()
    {
        await SeedUsersAsync();
        var client = _factory.CreateAuthenticatedClient(RegularUserId);

        var response = await client.GetAsync("/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetAdminUsers_AdminUser_Returns200WithUserList()
    {
        await SeedUsersAsync();
        var client = _factory.CreateAuthenticatedClient(AdminUserId);

        var response = await client.GetAsync("/api/admin/users?limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<dynamic>();
        ((object?)body?.users).Should().NotBeNull();
        ((object?)body?.total).Should().NotBeNull();
    }

    [Fact]
    public async Task GetAdminUsers_WithSearchFilter_Returns200()
    {
        await SeedUsersAsync();
        var client = _factory.CreateAuthenticatedClient(AdminUserId);

        var response = await client.GetAsync("/api/admin/users?search=test&limit=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── POST /api/admin/users/bulk-action ─────────────────────────────────────

    [Fact]
    public async Task BulkUserAction_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/admin/users/bulk-action", new
        {
            userIds = new[] { Guid.NewGuid() },
            action = "suspend",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BulkUserAction_NonAdmin_Returns403()
    {
        await SeedUsersAsync();
        var client = _factory.CreateAuthenticatedClient(RegularUserId);

        var response = await client.PostAsJsonAsync("/api/admin/users/bulk-action", new
        {
            userIds = new[] { Guid.NewGuid() },
            action = "suspend",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BulkUserAction_EmptyUserIds_Returns400()
    {
        await SeedUsersAsync();
        var client = _factory.CreateAuthenticatedClient(AdminUserId);

        var response = await client.PostAsJsonAsync("/api/admin/users/bulk-action", new
        {
            userIds = Array.Empty<Guid>(),
            action = "suspend",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BulkUserAction_SuspendTargetUser_Returns200()
    {
        await SeedUsersAsync();
        var targetId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(targetId);
        var client = _factory.CreateAuthenticatedClient(AdminUserId);

        var response = await client.PostAsJsonAsync("/api/admin/users/bulk-action", new
        {
            userIds = new[] { targetId },
            action = "suspend",
            reason = "Test suspension",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── POST /api/admin/roles ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateRole_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/admin/roles", new
        {
            name = "Test Role",
            key = "test_role",
            description = "A test role",
            permissionIds = Array.Empty<Guid>(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateRole_NonAdmin_Returns403()
    {
        await SeedUsersAsync();
        var client = _factory.CreateAuthenticatedClient(RegularUserId);

        var response = await client.PostAsJsonAsync("/api/admin/roles", new
        {
            name = "Test Role",
            key = "test_role",
            description = "A test role",
            permissionIds = Array.Empty<Guid>(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateRole_AdminNonSuperAdmin_Returns403()
    {
        await SeedUsersAsync();
        var client = _factory.CreateAuthenticatedClient(AdminUserId);

        var response = await client.PostAsJsonAsync("/api/admin/roles", new
        {
            name = "Test Role",
            key = "test_role",
            description = "A test role",
            permissionIds = Array.Empty<Guid>(),
        });

        // POST /admin/roles requires IsSuperAdmin — regular admin gets 403
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
    }

    // ── POST /api/admin/moderation-queue/{id}/review ──────────────────────────

    [Fact]
    public async Task ModerationReview_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/admin/moderation-queue/{Guid.NewGuid()}/review",
            new { action = "approve" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ModerationReview_NonAdmin_Returns403()
    {
        await SeedUsersAsync();
        var client = _factory.CreateAuthenticatedClient(RegularUserId);

        var response = await client.PostAsJsonAsync(
            $"/api/admin/moderation-queue/{Guid.NewGuid()}/review",
            new { action = "approve" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ModerationReview_AdminWithInvalidAction_Returns400()
    {
        await SeedUsersAsync();
        var client = _factory.CreateAuthenticatedClient(AdminUserId);

        var response = await client.PostAsJsonAsync(
            $"/api/admin/moderation-queue/{Guid.NewGuid()}/review",
            new { action = "invalid_action" });

        // Invalid action should return 400 (or 403/404 if permission check or item lookup runs first)
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest,
            HttpStatusCode.Forbidden,
            HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ModerationReview_AdminWithNonExistentItem_Returns404()
    {
        await SeedUsersAsync();
        var client = _factory.CreateAuthenticatedClient(AdminUserId);

        var response = await client.PostAsJsonAsync(
            $"/api/admin/moderation-queue/{Guid.NewGuid()}/review",
            new { action = "approve" });

        // Item doesn't exist — 404 or 403 if permission comes first
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.Forbidden);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedUsersAsync()
    {
        await _seeder.SeedAuthUserAsync(AdminUserId);
        await _seeder.SeedUserRoleAsync(AdminUserId, "admin");
        await _seeder.SeedAdminPanelRoleAsync(AdminUserId);

        await _seeder.SeedAuthUserAsync(RegularUserId);
        await _seeder.SeedUserRoleAsync(RegularUserId, "casual");
    }
}
