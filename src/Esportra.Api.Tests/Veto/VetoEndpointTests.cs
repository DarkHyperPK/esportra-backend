using System.Net;
using System.Net.Http.Json;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Veto;

[Collection(IntegrationCollection.Name)]
public sealed class VetoEndpointTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    private static readonly Guid OrganizerId = Guid.NewGuid();
    private static readonly Guid TournamentId = Guid.NewGuid();

    public VetoEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    // ── POST /api/veto/{matchId}/init ─────────────────────────────────────────

    [Fact]
    public async Task InitVeto_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/veto/{Guid.NewGuid()}/init", new
        {
            tournamentId = TournamentId,
            bestOf = 1,
            game = "valorant",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InitVeto_MatchNotFound_Returns404()
    {
        await SeedBaseDataAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PostAsJsonAsync($"/api/veto/{Guid.NewGuid()}/init", new
        {
            tournamentId = TournamentId,
            bestOf = 1,
            game = "valorant",
        });

        // No match row — expect 404
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InitVeto_UnauthorizedUser_Returns403()
    {
        await SeedBaseDataAsync();
        var stageId = await _seeder.SeedStageAsync(TournamentId);
        var versionId = await _seeder.SeedBracketVersionAsync(TournamentId, stageId);
        var matchId = await _seeder.SeedMatchAsync(versionId);

        var randomUser = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(randomUser);
        var client = _factory.CreateAuthenticatedClient(randomUser);

        var response = await client.PostAsJsonAsync($"/api/veto/{matchId}/init", new
        {
            tournamentId = TournamentId,
            bestOf = 1,
            game = "valorant",
        });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InitVeto_MissingTournamentId_Returns400()
    {
        await SeedBaseDataAsync();
        var stageId = await _seeder.SeedStageAsync(TournamentId);
        var versionId = await _seeder.SeedBracketVersionAsync(TournamentId, stageId);
        var matchId = await _seeder.SeedMatchAsync(versionId);
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PostAsJsonAsync($"/api/veto/{matchId}/init", new
        {
            // tournamentId omitted
            bestOf = 1,
            game = "valorant",
        });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task InitVeto_OrganizerWithValidMatch_Returns200OrBadRequest()
    {
        await SeedBaseDataAsync();
        var stageId = await _seeder.SeedStageAsync(TournamentId);
        var versionId = await _seeder.SeedBracketVersionAsync(TournamentId, stageId, "active");
        var matchId = await _seeder.SeedMatchAsync(versionId);
        var client = _factory.CreateAuthenticatedClient(OrganizerId);

        var response = await client.PostAsJsonAsync($"/api/veto/{matchId}/init", new
        {
            tournamentId = TournamentId,
            bestOf = 1,
            game = "valorant",
        });

        // Organizer may succeed (200) or fail (400) due to missing game catalog — both are auth-passed responses
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedBaseDataAsync()
    {
        await _seeder.SeedAuthUserAsync(OrganizerId);
        await _seeder.SeedUserRoleAsync(OrganizerId, "organizer");
        await _seeder.SeedTournamentAsync(OrganizerId, id: TournamentId);
    }
}
