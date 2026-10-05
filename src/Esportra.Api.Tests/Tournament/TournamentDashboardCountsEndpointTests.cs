using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Tournament;

/// <summary>
/// Integration tests for the 3 scheduling count fields added to GET /api/tournaments/{slugOrId}:
///   - unseededMatchCount       — round-0 matches with no teams placed
///   - unscheduledFirstRoundCount — round-0 matches with at least one team but no scheduled_time
///   - pendingEscalations        — matches escalated and still pending
///
/// Organizers receive real counts; non-organizers always receive zeros.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class TournamentDashboardCountsEndpointTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    // ── Scenario A: organizer receives real counts ─────────────────────────────
    private static readonly Guid OrganizerAId = Guid.NewGuid();
    private static readonly Guid TournamentAId = Guid.NewGuid();
    private static readonly Guid TeamAId = Guid.NewGuid();

    // ── Scenario B: non-organizer sees zeros ──────────────────────────────────
    private static readonly Guid OrganizerBId = Guid.NewGuid();
    private static readonly Guid NonOrganizerBId = Guid.NewGuid();
    private static readonly Guid TournamentBId = Guid.NewGuid();

    public TournamentDashboardCountsEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    // ── Organizer receives real counts ────────────────────────────────────────

    [Fact]
    public async Task GetTournament_AsOrganizer_ReturnsCorrectSchedulingCounts()
    {
        await SeedScenarioAAsync();

        var stageId = await _seeder.SeedStageAsync(TournamentAId, id: Guid.NewGuid());
        var versionId = await _seeder.SeedBracketVersionAsync(TournamentAId, stageId, "draft");

        // 1 unseeded: round_number=0, both teams NULL
        await _seeder.SeedMatchAsync(versionId, matchNumber: 1, roundNumber: 0, id: Guid.NewGuid());

        // 1 unscheduled: round_number=0, team1_id set, no scheduled_time
        await _seeder.SeedMatchAsync(versionId, team1Id: TeamAId, matchNumber: 2,
            roundNumber: 0, id: Guid.NewGuid());

        // 1 escalated pending: scheduling_escalated_at set, status='pending'
        await _seeder.SeedMatchAsync(versionId, matchStatus: "pending", matchNumber: 3,
            schedulingEscalatedAt: DateTime.UtcNow, id: Guid.NewGuid());

        var client = _factory.CreateAuthenticatedClient(OrganizerAId);
        var response = await client.GetAsync($"/api/tournaments/{TournamentAId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("unseededMatchCount").GetInt32().Should().Be(1);
        body.GetProperty("unscheduledFirstRoundCount").GetInt32().Should().Be(1);
        body.GetProperty("pendingEscalations").GetInt32().Should().Be(1);
    }

    // ── Non-organizer always receives zeros ───────────────────────────────────

    [Fact]
    public async Task GetTournament_AsNonOrganizer_ReturnsZeroSchedulingCounts()
    {
        await SeedScenarioBAsync();

        var stageId = await _seeder.SeedStageAsync(TournamentBId, id: Guid.NewGuid());
        var versionId = await _seeder.SeedBracketVersionAsync(TournamentBId, stageId, "active");

        // Seed a match that would show up in counts — should be invisible to non-organizer
        await _seeder.SeedMatchAsync(versionId, matchNumber: 1, roundNumber: 0, id: Guid.NewGuid());

        var client = _factory.CreateAuthenticatedClient(NonOrganizerBId);
        var response = await client.GetAsync($"/api/tournaments/{TournamentBId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("unseededMatchCount").GetInt32().Should().Be(0);
        body.GetProperty("unscheduledFirstRoundCount").GetInt32().Should().Be(0);
        body.GetProperty("pendingEscalations").GetInt32().Should().Be(0);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedScenarioAAsync()
    {
        await _seeder.SeedAuthUserAsync(OrganizerAId);
        await _seeder.SeedTournamentAsync(OrganizerAId, "Dashboard Counts A", id: TournamentAId);
    }

    private async Task SeedScenarioBAsync()
    {
        await _seeder.SeedAuthUserAsync(OrganizerBId);
        await _seeder.SeedAuthUserAsync(NonOrganizerBId);
        await _seeder.SeedTournamentAsync(OrganizerBId, "Dashboard Counts B", id: TournamentBId);

        // Make tournament public and ongoing so non-organizer can read it without 404
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync(
            """
            UPDATE public.tournaments
            SET is_public = TRUE, status = 'ongoing'::tournament_status
            WHERE id = @id
            """,
            new { id = TournamentBId });
    }
}
