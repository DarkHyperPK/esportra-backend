using System.Net;
using System.Net.Http.Json;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Licensing;

/// <summary>
/// Endpoint integration tests for event licensing gates (BE-4, BE-9, BE-10).
/// Auth and authorization path tests — no license-specific DB setup needed.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class EventLicenseEndpointTests
{
    private readonly ApiFactory _factory;

    public EventLicenseEndpointTests(ApiFactory factory) => _factory = factory;

    // ── BE-4: GET /api/my/event-licenses/quota ────────────────────────────────

    [Fact]
    public async Task GetQuota_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/my/event-licenses/quota");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetQuota_Authenticated_Returns200WithQuotaShape()
    {
        var userId = Guid.NewGuid();
        var client = _factory.CreateAuthenticatedClient(userId);

        var response = await client.GetAsync("/api/my/event-licenses/quota");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<QuotaResponse>();
        body.Should().NotBeNull();
        body!.MonthlyLimit.Should().Be(2);
        body.UsedThisMonth.Should().BeGreaterThanOrEqualTo(0);
        body.ResetsAt.Should().NotBeNullOrEmpty();
    }

    // ── BE-10: POST /api/admin/tournaments/{id}/organizer-pro ─────────────────

    [Fact]
    public async Task SetOrganizerPro_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/admin/tournaments/{Guid.NewGuid()}/organizer-pro",
            new { enabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SetOrganizerPro_NonAdmin_Returns403()
    {
        // A regular authenticated user (no admin role) should be rejected.
        var userId = Guid.NewGuid();
        var client = _factory.CreateAuthenticatedClient(userId);

        var response = await client.PostAsJsonAsync(
            $"/api/admin/tournaments/{Guid.NewGuid()}/organizer-pro",
            new { enabled = true });

        // 403 from RequireAuthorization("admin") policy
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── BE-9: POST /api/broadcast/sessions ────────────────────────────────────

    [Fact]
    public async Task CreateBroadcastSession_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Version", "0.1.0");

        var response = await client.PostAsJsonAsync("/api/broadcast/sessions", new
        {
            matchCode = "TESTCODE",
            seriesTotal = 1,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── BE-5: PUT /api/veto/{matchId}/settings ────────────────────────────────

    [Fact]
    public async Task VetoSettings_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            $"/api/veto/{Guid.NewGuid()}/settings",
            new { mode = "custom" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

// ── Response shape ────────────────────────────────────────────────────────────

file sealed record QuotaResponse(int UsedThisMonth, int MonthlyLimit, string? ResetsAt, bool IsOrganizerPro);
