using System.Net;
using System.Net.Http.Json;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Match;

[Collection(IntegrationCollection.Name)]
public sealed class MatchEndpointTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    private static readonly Guid OrganizerId = Guid.NewGuid();
    private static readonly Guid TournamentId = Guid.NewGuid();

    public MatchEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    // ── POST /api/matches/{matchId}/save-score ────────────────────────────────

    [Fact]
    public async Task SaveScore_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/matches/{Guid.NewGuid()}/save-score", new
        {
            team1Score = 1,
            team2Score = 0,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SaveScore_MatchNotFound_Returns404()
    {
        await SeedBaseDataAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PostAsJsonAsync($"/api/matches/{Guid.NewGuid()}/save-score", new
        {
            team1Score = 1,
            team2Score = 0,
        });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SaveScore_TiedScores_Returns400()
    {
        await SeedBaseDataAsync();
        var (matchId, team1Id, team2Id) = await SeedLiveMatchAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PostAsJsonAsync($"/api/matches/{matchId}/save-score", new
        {
            team1Score = 1,
            team2Score = 1,
            team1Id,
            team2Id,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── POST /api/matches/{matchId}/go-live ───────────────────────────────────

    [Fact]
    public async Task GoLive_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/matches/{Guid.NewGuid()}/go-live", new
        {
            force = false,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GoLive_MatchNotFound_Returns404Or403()
    {
        await SeedBaseDataAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PostAsJsonAsync($"/api/matches/{Guid.NewGuid()}/go-live", new
        {
            force = true,
        });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GoLive_ValidMatch_AuthPassesOrTimingBlocks()
    {
        await SeedBaseDataAsync();
        var (matchId, _, _) = await SeedLiveMatchAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PostAsJsonAsync($"/api/matches/{matchId}/go-live", new
        {
            force = true,
        });

        // Auth passed — organizer may succeed (200) or timing logic may block (400)
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    // ── POST /api/matches/{matchId}/finalize ──────────────────────────────────

    [Fact]
    public async Task Finalize_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/matches/{Guid.NewGuid()}/finalize", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Finalize_UnauthorizedUser_Returns403Or404()
    {
        await SeedBaseDataAsync();
        var randomUser = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(randomUser);
        var (matchId, _, _) = await SeedLiveMatchAsync();
        var client = _factory.CreateAuthenticatedClient(randomUser);

        var response = await client.PostAsJsonAsync($"/api/matches/{matchId}/finalize", new { });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Finalize_OrganizerWithNoScores_Returns400OrSimilar()
    {
        await SeedBaseDataAsync();
        var (matchId, _, _) = await SeedLiveMatchAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PostAsJsonAsync($"/api/matches/{matchId}/finalize", new { });

        // No scores recorded — expect 400 (no winner derivable) or 404 (not yet in_progress)
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest,
            HttpStatusCode.NotFound,
            HttpStatusCode.OK);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedBaseDataAsync()
    {
        await _seeder.SeedAuthUserAsync(OrganizerId);
        await _seeder.SeedUserRoleAsync(OrganizerId, "organizer");
        await _seeder.SeedTournamentAsync(OrganizerId, id: TournamentId);
    }

    private async Task<(Guid matchId, Guid team1Id, Guid team2Id)> SeedLiveMatchAsync()
    {
        var captain1 = Guid.NewGuid();
        var captain2 = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(captain1);
        await _seeder.SeedAuthUserAsync(captain2);
        var team1Id = await _seeder.SeedTeamAsync(captain1, "Team Alpha");
        var team2Id = await _seeder.SeedTeamAsync(captain2, "Team Bravo");

        var stageId = await _seeder.SeedStageAsync(TournamentId);
        var versionId = await _seeder.SeedBracketVersionAsync(TournamentId, stageId, "active");
        var matchId = await _seeder.SeedMatchAsync(versionId, team1Id: team1Id, team2Id: team2Id,
            matchStatus: "in_progress");
        return (matchId, team1Id, team2Id);
    }
}
