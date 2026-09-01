using System.Net;
using System.Net.Http.Json;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Bracket;

[Collection(IntegrationCollection.Name)]
public sealed class BracketEndpointTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    private static readonly Guid OrganizerId = Guid.NewGuid();
    private static readonly Guid TournamentId = Guid.NewGuid();

    public BracketEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    // ── POST /api/brackets/generate ──────────────────────────────────────────

    [Fact]
    public async Task GenerateBracket_ValidSingleElimRequest_Returns200WithVersionId()
    {
        await SeedBaseDataAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var team1 = Guid.NewGuid();
        var team2 = Guid.NewGuid();
        var stageId = await _seeder.SeedStageAsync(TournamentId);

        var response = await client.PostAsJsonAsync("/api/brackets/generate", new
        {
            tournamentId = TournamentId,
            stageId,
            format = "single_elimination",
            teams = new[]
            {
                new { id = team1, name = "Alpha" },
                new { id = team2, name = "Bravo" },
            },
            bestOf = 1,
            advancementCount = 1,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<dynamic>();
        ((object?)body?.versionId).Should().NotBeNull();
    }

    [Fact]
    public async Task GenerateBracket_MissingTournamentId_Returns400()
    {
        await SeedBaseDataAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PostAsJsonAsync("/api/brackets/generate", new
        {
            format = "single_elimination",
            teams = new[] { new { id = Guid.NewGuid(), name = "Alpha" } },
            bestOf = 1,
            advancementCount = 1,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GenerateBracket_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/brackets/generate", new
        {
            tournamentId = TournamentId,
            format = "single_elimination",
            teams = new[] { new { id = Guid.NewGuid(), name = "Alpha" } },
            bestOf = 1,
            advancementCount = 1,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /api/stages/{stageId}/seed-bracket ───────────────────────────────

    [Fact]
    public async Task SeedBracket_NonExistentStage_Returns403Or404()
    {
        await SeedBaseDataAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PostAsJsonAsync($"/api/stages/{Guid.NewGuid()}/seed-bracket", new { });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SeedBracket_Unauthenticated_Returns401()
    {
        var stageId = await _seeder.SeedStageAsync(TournamentId);
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/stages/{stageId}/seed-bracket", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SeedBracket_StageWithNoBracketVersion_Returns404OrError()
    {
        await SeedBaseDataAsync();
        var stageId = await _seeder.SeedStageAsync(TournamentId);
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PostAsJsonAsync($"/api/stages/{stageId}/seed-bracket", new { });

        // No bracket version exists yet — expect 404 or 400
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.BadRequest,
            HttpStatusCode.Forbidden);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedBaseDataAsync()
    {
        await _seeder.SeedAuthUserAsync(OrganizerId);
        await _seeder.SeedUserRoleAsync(OrganizerId, "organizer");
        await _seeder.SeedTournamentAsync(OrganizerId, id: TournamentId);
    }
}
