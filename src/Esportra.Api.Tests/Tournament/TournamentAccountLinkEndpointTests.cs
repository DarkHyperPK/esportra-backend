using System.Net;
using System.Net.Http.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Tournament;

[Collection(IntegrationCollection.Name)]
public sealed class TournamentAccountLinkEndpointTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    // Ongoing tournament — RequiredAccountLinks=2, AssistedReporting=true
    private static readonly Guid OrganizerA = Guid.NewGuid();
    private static readonly Guid OngoingTournamentId = Guid.NewGuid();

    // Open tournament, past registration deadline — RequiredAccountLinks=2
    private static readonly Guid OrganizerB = Guid.NewGuid();
    private static readonly Guid OpenPastDeadlineTournamentId = Guid.NewGuid();

    // Draft tournament, future registration deadline — RequiredAccountLinks=2
    private static readonly Guid OrganizerC = Guid.NewGuid();
    private static readonly Guid DraftFutureTournamentId = Guid.NewGuid();

    // Ongoing tournament — RequiredAccountLinks=2, AssistedReporting=false (for enable-block test)
    private static readonly Guid OrganizerD = Guid.NewGuid();
    private static readonly Guid OngoingArDisabledTournamentId = Guid.NewGuid();

    private const string TwoLinksArEnabledSettings =
        """{"requiredAccountLinks": 2, "assistedReportingEnabled": true}""";

    private const string TwoLinksArDisabledSettings =
        """{"requiredAccountLinks": 2, "assistedReportingEnabled": false}""";

    public TournamentAccountLinkEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    // ── Ongoing tournament — RequiredAccountLinks 2, AssistedReporting true ──

    [Fact]
    public async Task UpdateTournament_AllowsLoweringRequiredAccountLinks_WhenOngoing()
    {
        await SeedOngoingTournamentAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerA);

        var response = await client.PutAsJsonAsync(
            $"/api/tournaments/{OngoingTournamentId}",
            new { requiredAccountLinks = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UpdateTournament_AllowsDisablingRequiredAccountLinks_WhenOngoing()
    {
        await SeedOngoingTournamentAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerA);

        var response = await client.PutAsJsonAsync(
            $"/api/tournaments/{OngoingTournamentId}",
            new { requiredAccountLinks = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UpdateTournament_BlocksRaisingRequiredAccountLinks_WhenOngoing()
    {
        await SeedOngoingTournamentAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerA);

        var response = await client.PutAsJsonAsync(
            $"/api/tournaments/{OngoingTournamentId}",
            new { requiredAccountLinks = 3 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UpdateTournament_AllowsDisablingAssistedReporting_WhenOngoing()
    {
        await SeedOngoingTournamentAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerA);

        var response = await client.PutAsJsonAsync(
            $"/api/tournaments/{OngoingTournamentId}",
            new { assistedReportingEnabled = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UpdateTournament_BlocksEnablingAssistedReporting_WhenOngoing()
    {
        await SeedOngoingArDisabledTournamentAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerD);

        var response = await client.PutAsJsonAsync(
            $"/api/tournaments/{OngoingArDisabledTournamentId}",
            new { assistedReportingEnabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AllowsResendingAssistedReportingTrue_WhenAlreadyEnabled_Ongoing()
    {
        await SeedOngoingTournamentAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerA);

        var response = await client.PutAsJsonAsync(
            $"/api/tournaments/{OngoingTournamentId}",
            new { assistedReportingEnabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UpdateTournament_BlocksRequest_WhenAnyFieldTightens()
    {
        await SeedOngoingTournamentAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerA);

        // AssistedReportingEnabled=false is not tightening, but RequiredAccountLinks=5
        // raises above the existing value — the block fires on the raising field alone.
        var response = await client.PutAsJsonAsync(
            $"/api/tournaments/{OngoingTournamentId}",
            new { assistedReportingEnabled = false, requiredAccountLinks = 5 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UpdateTournament_AllowsDiscordLinkCount_WhenOngoing()
    {
        await SeedOngoingTournamentAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerA);

        var response = await client.PutAsJsonAsync(
            $"/api/tournaments/{OngoingTournamentId}",
            new { discordLinkCount = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
    }

    // ── Open tournament, past registration deadline ───────────────────────────

    [Fact]
    public async Task UpdateTournament_BlocksRaisingAccountLinks_WhenPastDeadline_OpenStatus()
    {
        await SeedOpenPastDeadlineTournamentAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerB);

        var response = await client.PutAsJsonAsync(
            $"/api/tournaments/{OpenPastDeadlineTournamentId}",
            new { requiredAccountLinks = 3 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UpdateTournament_AllowsLoweringAccountLinks_WhenPastDeadline_OpenStatus()
    {
        await SeedOpenPastDeadlineTournamentAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerB);

        var response = await client.PutAsJsonAsync(
            $"/api/tournaments/{OpenPastDeadlineTournamentId}",
            new { requiredAccountLinks = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
    }

    // ── Draft tournament, future registration deadline ────────────────────────

    [Fact]
    public async Task UpdateTournament_AllowsRaisingAccountLinks_WhenDraft_BeforeDeadline()
    {
        await SeedDraftFutureTournamentAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerC);

        var response = await client.PutAsJsonAsync(
            $"/api/tournaments/{DraftFutureTournamentId}",
            new { requiredAccountLinks = 3 });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task SeedOngoingTournamentAsync()
    {
        await _seeder.SeedAuthUserAsync(OrganizerA);
        await _seeder.SeedUserRoleAsync(OrganizerA, "organizer");
        await SeedValorantTournamentAsync(
            OrganizerA, OngoingTournamentId, "ongoing",
            TwoLinksArEnabledSettings, pastDeadline: true);
    }

    private async Task SeedOpenPastDeadlineTournamentAsync()
    {
        await _seeder.SeedAuthUserAsync(OrganizerB);
        await _seeder.SeedUserRoleAsync(OrganizerB, "organizer");
        await SeedValorantTournamentAsync(
            OrganizerB, OpenPastDeadlineTournamentId, "open",
            TwoLinksArEnabledSettings, pastDeadline: true);
    }

    private async Task SeedDraftFutureTournamentAsync()
    {
        await _seeder.SeedAuthUserAsync(OrganizerC);
        await _seeder.SeedUserRoleAsync(OrganizerC, "organizer");
        await SeedValorantTournamentAsync(
            OrganizerC, DraftFutureTournamentId, "draft",
            TwoLinksArEnabledSettings, pastDeadline: false);
    }

    private async Task SeedOngoingArDisabledTournamentAsync()
    {
        await _seeder.SeedAuthUserAsync(OrganizerD);
        await _seeder.SeedUserRoleAsync(OrganizerD, "organizer");
        await SeedValorantTournamentAsync(
            OrganizerD, OngoingArDisabledTournamentId, "ongoing",
            TwoLinksArDisabledSettings, pastDeadline: true);
    }

    private async Task SeedValorantTournamentAsync(
        Guid organizerId,
        Guid tournamentId,
        string status,
        string settingsJson,
        bool pastDeadline)
    {
        var start = DateTimeOffset.UtcNow.AddDays(7);
        var end = start.AddDays(7);
        var deadline = pastDeadline
            ? DateTimeOffset.UtcNow.AddDays(-1)
            : DateTimeOffset.UtcNow.AddDays(1);

        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO public.tournaments
                (id, name, game, game_mode, format, team_size, status, organizer_id, max_teams,
                 is_public, slug, start_date, end_date, registration_deadline,
                 settings, created_at, updated_at)
            VALUES
                (@id, @name, 'valorant', '5v5', 'single_elimination', 5,
                 @status::tournament_status, @organizerId, 8,
                 FALSE, @slug, @start, @end, @deadline,
                 @settings::jsonb, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new
            {
                id = tournamentId,
                name = $"AL Test {tournamentId:N}"[..20],
                status,
                organizerId,
                slug = $"al-{tournamentId:N}"[..20],
                start,
                end,
                deadline,
                settings = settingsJson,
            });
    }
}
