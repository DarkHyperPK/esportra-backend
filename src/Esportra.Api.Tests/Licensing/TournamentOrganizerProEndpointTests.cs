using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Licensing;

/// <summary>
/// GET /api/tournaments/{id}/organizer-pro returns the same per-tournament rule the gated
/// endpoints enforce: Pro enabled on the tournament's license, or no license row (grandfathered).
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class TournamentOrganizerProEndpointTests(ApiFactory factory)
{
    private async Task<(Guid OrganizerId, Guid TournamentId)> SeedTournamentAsync()
    {
        var organizerId = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(organizerId);
        var tournamentId = await factory.Seeder.SeedTournamentAsync(organizerId);
        return (organizerId, tournamentId);
    }

    private async Task SeedLicenseAsync(Guid organizerId, Guid tournamentId, bool hasPro)
    {
        await using var conn = factory.Seeder.OpenConnection();
        var orgId = await conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO organizations (owner_id, name, slug) VALUES (@organizerId, 'Pro Test Org', @slug) RETURNING id",
            new { organizerId, slug = $"pro-test-{Guid.NewGuid():N}"[..24] });
        await conn.ExecuteAsync(
            """
            INSERT INTO event_licenses (tournament_id, user_id, organization_id, has_organizer_pro, is_free_quota)
            VALUES (@tournamentId, @organizerId, @orgId, @hasPro, FALSE)
            """,
            new { tournamentId, organizerId, orgId, hasPro });
    }

    private static async Task<bool> ReadProAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("isOrganizerPro").GetBoolean();
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        var (_, tournamentId) = await SeedTournamentAsync();

        var response = await factory.CreateClient().GetAsync($"/api/tournaments/{tournamentId}/organizer-pro");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnrelatedUser_Returns403()
    {
        var (_, tournamentId) = await SeedTournamentAsync();
        var strangerId = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(strangerId);

        var response = await factory.CreateAuthenticatedClient(strangerId)
            .GetAsync($"/api/tournaments/{tournamentId}/organizer-pro");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UnknownTournament_Returns404()
    {
        var userId = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(userId);

        var response = await factory.CreateAuthenticatedClient(userId)
            .GetAsync($"/api/tournaments/{Guid.NewGuid()}/organizer-pro");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Organizer_NoLicenseRow_IsGrandfatheredAsPro()
    {
        var (organizerId, tournamentId) = await SeedTournamentAsync();

        var response = await factory.CreateAuthenticatedClient(organizerId)
            .GetAsync($"/api/tournaments/{tournamentId}/organizer-pro");

        (await ReadProAsync(response)).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Organizer_LicenseRow_ReturnsItsProFlag(bool hasPro)
    {
        var (organizerId, tournamentId) = await SeedTournamentAsync();
        await SeedLicenseAsync(organizerId, tournamentId, hasPro);

        var response = await factory.CreateAuthenticatedClient(organizerId)
            .GetAsync($"/api/tournaments/{tournamentId}/organizer-pro");

        (await ReadProAsync(response)).Should().Be(hasPro);
    }
}
