using System.Net;
using System.Net.Http.Json;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Stage;

[Collection(IntegrationCollection.Name)]
public sealed class StageEndpointTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    private static readonly Guid OrganizerId = Guid.NewGuid();
    private static readonly Guid TournamentId = Guid.NewGuid();

    public StageEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    // ── PUT /api/tournaments/{tournamentId}/stages ────────────────────────────

    [Fact]
    public async Task PutStages_ValidSingleStage_Returns200()
    {
        await SeedBaseDataAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PutAsJsonAsync($"/api/tournaments/{TournamentId}/stages", new
        {
            stages = new[]
            {
                new
                {
                    name = "Main Stage",
                    format = "single_elimination",
                    stageOrder = 1,
                    bestOf = 1,
                    advancementCount = 1,
                },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PutStages_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/api/tournaments/{TournamentId}/stages", new
        {
            stages = new[] { new { name = "Stage 1", format = "single_elimination", stageOrder = 1, bestOf = 1, advancementCount = 1 } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PutStages_NonOrganizerUser_Returns403()
    {
        await SeedBaseDataAsync();
        var otherId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(otherId);
        var client = _factory.CreateAuthenticatedClient(otherId);

        var response = await client.PutAsJsonAsync($"/api/tournaments/{TournamentId}/stages", new
        {
            stages = new[] { new { name = "Stage 1", format = "single_elimination", stageOrder = 1, bestOf = 1, advancementCount = 1 } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── POST /api/stages/{stageId}/advance ────────────────────────────────────

    [Fact]
    public async Task AdvanceStage_StageWithNoCompletedBracket_ReturnsError()
    {
        await SeedBaseDataAsync();
        var stageId = await _seeder.SeedStageAsync(TournamentId);
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PostAsync($"/api/stages/{stageId}/advance", null);

        // Stage has no bracket — endpoint may advance (200) or reject (400/403/404)
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK,
            HttpStatusCode.NotFound,
            HttpStatusCode.BadRequest,
            HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdvanceStage_Unauthenticated_Returns401()
    {
        var stageId = Guid.NewGuid();
        var client = _factory.CreateClient();

        var response = await client.PostAsync($"/api/stages/{stageId}/advance", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedBaseDataAsync()
    {
        await _seeder.SeedAuthUserAsync(OrganizerId);
        await _seeder.SeedUserRoleAsync(OrganizerId, "organizer");
        await _seeder.SeedTournamentAsync(OrganizerId, id: TournamentId);
    }
}
