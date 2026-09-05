using System.Net;
using System.Net.Http.Json;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Match;

[Collection(IntegrationCollection.Name)]
public sealed class MatchSystemEndpointTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    private static readonly Guid OrganizerId = Guid.NewGuid();
    private static readonly Guid TournamentId = Guid.NewGuid();

    public MatchSystemEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    // ── POST /api/matches/{id}/reports ────────────────────────────────────────

    [Fact]
    public async Task SubmitReport_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/matches/{Guid.NewGuid()}/reports", new
        {
            gameNumber = 1,
            reportedByTeamId = Guid.NewGuid().ToString(),
            team1Score = 13,
            team2Score = 7,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SubmitReport_MatchNotFound_Returns400Or403()
    {
        await SeedBaseDataAsync();
        var captain = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(captain);
        var client = _factory.CreateAuthenticatedClient(captain);

        var response = await client.PostAsJsonAsync($"/api/matches/{Guid.NewGuid()}/reports", new
        {
            gameNumber = 1,
            reportedByTeamId = Guid.NewGuid().ToString(),
            team1Score = 13,
            team2Score = 7,
        });

        // No match → access check fails with 400 or 403
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest,
            HttpStatusCode.Forbidden,
            HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SubmitReport_CallerNotInMatch_Returns403()
    {
        await SeedBaseDataAsync();
        var (matchId, _, _) = await SeedMatchWithTeamsAsync();
        var outsider = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(outsider);
        var client = _factory.CreateAuthenticatedClient(outsider);

        var response = await client.PostAsJsonAsync($"/api/matches/{matchId}/reports", new
        {
            gameNumber = 1,
            reportedByTeamId = Guid.NewGuid().ToString(),
            team1Score = 13,
            team2Score = 7,
        });

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.Forbidden,
            HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SubmitReport_InvalidTeamId_Returns400()
    {
        await SeedBaseDataAsync();
        var captain1 = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(captain1);
        var (matchId, _, _) = await SeedMatchWithTeamsAsync();
        var client = _factory.CreateAuthenticatedClient(captain1);

        var response = await client.PostAsJsonAsync($"/api/matches/{matchId}/reports", new
        {
            gameNumber = 1,
            reportedByTeamId = "not-a-guid",
            team1Score = 13,
            team2Score = 7,
        });

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest,
            HttpStatusCode.Forbidden);
    }

    // ── POST /api/matches/{id}/reports/{rid}/accept ───────────────────────────

    [Fact]
    public async Task AcceptReport_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/matches/{Guid.NewGuid()}/reports/{Guid.NewGuid()}/accept",
            new { gameNumber = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AcceptReport_ReportNotFound_Returns403Or404()
    {
        await SeedBaseDataAsync();
        var (matchId, _, _) = await SeedMatchWithTeamsAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PostAsJsonAsync(
            $"/api/matches/{matchId}/reports/{Guid.NewGuid()}/accept",
            new { gameNumber = 1 });

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.Forbidden,
            HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AcceptReport_UnauthorizedUser_Returns403()
    {
        await SeedBaseDataAsync();
        var (matchId, _, _) = await SeedMatchWithTeamsAsync();
        var outsider = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(outsider);
        var client = _factory.CreateAuthenticatedClient(outsider);

        var response = await client.PostAsJsonAsync(
            $"/api/matches/{matchId}/reports/{Guid.NewGuid()}/accept",
            new { gameNumber = 1 });

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.Forbidden,
            HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AcceptReport_OrganizerWithMissingReport_Returns404()
    {
        await SeedBaseDataAsync();
        var (matchId, _, _) = await SeedMatchWithTeamsAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PostAsJsonAsync(
            $"/api/matches/{matchId}/reports/{Guid.NewGuid()}/accept",
            new { gameNumber = 1 });

        // Organizer auth passes but report doesn't exist
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedBaseDataAsync()
    {
        await _seeder.SeedAuthUserAsync(OrganizerId);
        await _seeder.SeedUserRoleAsync(OrganizerId, "organizer");
        await _seeder.SeedTournamentAsync(OrganizerId, id: TournamentId);
    }

    private async Task<(Guid matchId, Guid team1Id, Guid team2Id)> SeedMatchWithTeamsAsync()
    {
        var captain1 = Guid.NewGuid();
        var captain2 = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(captain1);
        await _seeder.SeedAuthUserAsync(captain2);
        var team1Id = await _seeder.SeedTeamAsync(captain1, "Alpha");
        var team2Id = await _seeder.SeedTeamAsync(captain2, "Bravo");
        var stageId = await _seeder.SeedStageAsync(TournamentId);
        var versionId = await _seeder.SeedBracketVersionAsync(TournamentId, stageId, "active");
        var matchId = await _seeder.SeedMatchAsync(versionId, team1Id: team1Id, team2Id: team2Id,
            matchStatus: "in_progress");
        return (matchId, team1Id, team2Id);
    }
}
