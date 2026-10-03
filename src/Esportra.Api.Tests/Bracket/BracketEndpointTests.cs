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
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("versionId").GetGuid().Should().NotBe(Guid.Empty);
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
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/stages/{Guid.NewGuid()}/seed-bracket", new { });

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

    [Fact]
    public async Task SeedBracket_1v1FinalBracketType_Seeds1Participant()
    {
        // 1v1 brackets have bracket_type='final' on their only match (round 0).
        // Before the fix, seeding returned seeded:0 because the query filtered only 'winners'.
        var tournamentId = Guid.NewGuid();
        var participantUserId = Guid.NewGuid();

        await _seeder.SeedAuthUserAsync(OrganizerId);
        await _seeder.SeedUserRoleAsync(OrganizerId, "organizer");
        await _seeder.SeedTournamentAsync(OrganizerId, id: tournamentId);
        await _seeder.SeedAuthUserAsync(participantUserId);
        await _seeder.SeedParticipantAsync(participantUserId, tournamentId, "approved");

        var stageId = await _seeder.SeedStageAsync(tournamentId);
        var versionId = await _seeder.SeedBracketVersionAsync(tournamentId, stageId);
        await _seeder.SeedMatchAsync(versionId, bracketType: "final", roundIndex: 0, matchNumber: 1);

        var client = _factory.CreateAuthenticatedClient(OrganizerId);
        var response = await client.PostAsJsonAsync($"/api/stages/{stageId}/seed-bracket", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("seeded").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task SeedBracket_StandardWinnersBracketType_SeedsParticipants()
    {
        var tournamentId = Guid.NewGuid();
        var p1UserId = Guid.NewGuid();
        var p2UserId = Guid.NewGuid();

        await _seeder.SeedAuthUserAsync(OrganizerId);
        await _seeder.SeedUserRoleAsync(OrganizerId, "organizer");
        await _seeder.SeedTournamentAsync(OrganizerId, id: tournamentId);
        await _seeder.SeedAuthUserAsync(p1UserId);
        await _seeder.SeedAuthUserAsync(p2UserId);
        await _seeder.SeedParticipantAsync(p1UserId, tournamentId, "approved");
        await _seeder.SeedParticipantAsync(p2UserId, tournamentId, "approved");

        var stageId = await _seeder.SeedStageAsync(tournamentId);
        var versionId = await _seeder.SeedBracketVersionAsync(tournamentId, stageId);
        await _seeder.SeedMatchAsync(versionId, bracketType: "winners", roundIndex: 0, matchNumber: 1);

        var client = _factory.CreateAuthenticatedClient(OrganizerId);
        var response = await client.PostAsJsonAsync($"/api/stages/{stageId}/seed-bracket", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("seeded").GetInt32().Should().Be(2);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedBaseDataAsync()
    {
        await _seeder.SeedAuthUserAsync(OrganizerId);
        await _seeder.SeedUserRoleAsync(OrganizerId, "organizer");
        await _seeder.SeedTournamentAsync(OrganizerId, id: TournamentId);
    }
}
