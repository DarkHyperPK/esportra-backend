using System.Net;
using System.Net.Http.Json;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Tournament;

[Collection(TestDatabaseCollection.Name)]
public sealed class DisputeEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TournamentId = Guid.NewGuid();

    public DisputeEndpointTests(TestDatabase db)
    {
        _factory = new ApiFactory(db);
        _seeder = _factory.Seeder;
    }

    // ── POST /api/disputes ────────────────────────────────────────────────────

    [Fact]
    public async Task PostDispute_WithSnakeCaseFields_CreatesDisputeAndReturns201()
    {
        await SeedBaseDataAsync();
        var client = _factory.CreateAuthenticatedClient(UserId);

        var response = await client.PostAsJsonAsync("/api/disputes", new
        {
            tournament_id = TournamentId,
            title = "Score dispute",
            description = "The opponent disconnected and we couldn't finish.",
            dispute_reason = "technical_issue",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<dynamic>();
        ((string?)body?.status).Should().Be("open");
        ((string?)body?.reference_number).Should().StartWith("DSP-");
    }

    [Fact]
    public async Task PostDispute_WithCamelCaseFields_CreatesDispute()
    {
        await SeedBaseDataAsync();
        var client = _factory.CreateAuthenticatedClient(UserId);

        var response = await client.PostAsJsonAsync("/api/disputes", new
        {
            tournamentId = TournamentId,
            title = "Map veto dispute",
            description = "Wrong map was picked during veto.",
            reason = "rule_violation",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task PostDispute_MissingTournamentId_Returns400()
    {
        await SeedBaseDataAsync();
        var client = _factory.CreateAuthenticatedClient(UserId);

        var response = await client.PostAsJsonAsync("/api/disputes", new
        {
            title = "No tournament",
            description = "Missing tournament_id field",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<dynamic>();
        ((string?)body?.error).Should().Contain("tournament_id");
    }

    [Fact]
    public async Task PostDispute_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/disputes", new
        {
            tournament_id = TournamentId,
            title = "Dispute",
            description = "Unauthenticated attempt",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedBaseDataAsync()
    {
        await _seeder.SeedAuthUserAsync(UserId);
        await _seeder.SeedUserRoleAsync(UserId, "player");
        await _seeder.SeedTournamentAsync(UserId, id: TournamentId);
    }
}
