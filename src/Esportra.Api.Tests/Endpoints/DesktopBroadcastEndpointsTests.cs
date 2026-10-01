using System.Net;
using System.Net.Http.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

/// <summary>
/// Integration tests for DesktopBroadcastEndpoints (PROJ-043 Phase 1).
/// Covers: T-008 session CRUD, T-009 active-match, T-010 companion stat post, T-036 version gate.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class DesktopBroadcastEndpointsTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    private static readonly Guid OrganizerId = Guid.NewGuid();
    private static readonly Guid PlayerId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();

    public DesktopBroadcastEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    // ── T-036: Version gate ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateSession_MissingVersionHeader_Returns426()
    {
        await SeedBaseDataAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerId);
        // No X-Client-Version header

        var response = await client.PostAsJsonAsync("/api/broadcast/sessions", new
        {
            matchCode = "GF1",
            seriesTotal = 1,
        });

        response.StatusCode.Should().Be((HttpStatusCode)426);
    }

    [Fact]
    public async Task CreateSession_OutdatedVersionHeader_Returns426()
    {
        await SeedBaseDataAsync();
        var client = _factory.CreateAuthenticatedClient(OrganizerId);
        client.DefaultRequestHeaders.Add("X-Client-Version", "0.0.1");

        var response = await client.PostAsJsonAsync("/api/broadcast/sessions", new
        {
            matchCode = "GF1",
            seriesTotal = 1,
        });

        response.StatusCode.Should().Be((HttpStatusCode)426);
    }

    // ── T-008: POST /api/broadcast/sessions ───────────────────────────────────

    [Fact]
    public async Task CreateSession_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/broadcast/sessions", new
        {
            matchCode = "GF1",
            seriesTotal = 1,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateSession_NonOrganizer_Returns403()
    {
        await SeedBaseDataAsync();
        var client = CreateDesktopClient(PlayerId);

        var response = await client.PostAsJsonAsync("/api/broadcast/sessions", new
        {
            matchCode = "GF1",
            seriesTotal = 1,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateSession_ValidOrganizerRequest_ReturnsSessionId()
    {
        await SeedBaseDataAsync();
        var client = CreateDesktopClient(OrganizerId);

        var response = await client.PostAsJsonAsync("/api/broadcast/sessions", new
        {
            matchCode = "SF1",
            seriesTotal = 3,
            appVersion = "0.1.0",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<dynamic>();
        Assert.NotNull(body);
    }

    // ── T-008: GET /api/broadcast/sessions ────────────────────────────────────

    [Fact]
    public async Task GetSessions_NonOrganizer_Returns403()
    {
        await SeedBaseDataAsync();
        var client = CreateDesktopClient(PlayerId);

        var response = await client.GetAsync("/api/broadcast/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetSessions_OrganizerWithNoSessions_ReturnsEmptyList()
    {
        await SeedBaseDataAsync();
        var client = CreateDesktopClient(OrganizerId);

        var response = await client.GetAsync("/api/broadcast/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── T-008: POST /api/broadcast/sessions/{sessionId}/game-result ──────────

    [Fact]
    public async Task GameResult_SessionNotFound_Returns404()
    {
        await SeedBaseDataAsync();
        var client = CreateDesktopClient(OrganizerId);

        var response = await client.PostAsJsonAsync(
            $"/api/broadcast/sessions/{Guid.NewGuid()}/game-result",
            new { mapNumber = 1, teamAScore = 13, teamBScore = 7 });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GameResult_SessionOwnedByOtherOrganizer_Returns403()
    {
        await SeedBaseDataAsync();
        var otherOrganizerId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(otherOrganizerId);
        await _seeder.SeedUserRoleAsync(otherOrganizerId, "organizer");

        // Create session as otherOrganizer
        var otherClient = CreateDesktopClient(otherOrganizerId);
        var createResp = await otherClient.PostAsJsonAsync("/api/broadcast/sessions", new
        {
            matchCode = "R1",
            seriesTotal = 1,
        });
        createResp.EnsureSuccessStatusCode();

        var created = await createResp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var sessionId = created.GetProperty("sessionId").GetGuid();

        // Try to write game result as the original organizer (different user)
        var client = CreateDesktopClient(OrganizerId);
        var response = await client.PostAsJsonAsync(
            $"/api/broadcast/sessions/{sessionId}/game-result",
            new { mapNumber = 1, teamAScore = 13, teamBScore = 7 });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GameResult_Idempotent_SameScoresReturnOk()
    {
        await SeedBaseDataAsync();
        var client = CreateDesktopClient(OrganizerId);

        // Create session
        var createResp = await client.PostAsJsonAsync("/api/broadcast/sessions", new
        {
            matchCode = "IDEM1",
            seriesTotal = 1,
        });
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var sessionId = created.GetProperty("sessionId").GetGuid();

        // Attach a match to the session via direct DB
        await AttachMatchToSessionAsync(sessionId);

        var payload = new { mapNumber = 1, teamAScore = 13, teamBScore = 7 };

        // First call
        var resp1 = await client.PostAsJsonAsync(
            $"/api/broadcast/sessions/{sessionId}/game-result", payload);
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second call — same scores — should be idempotent (200)
        var resp2 = await client.PostAsJsonAsync(
            $"/api/broadcast/sessions/{sessionId}/game-result", payload);
        resp2.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── T-009: GET /api/tournaments/active-match ─────────────────────────────

    [Fact]
    public async Task ActiveMatch_NoRiotAccount_ReturnsNullMatchId()
    {
        await SeedBaseDataAsync();
        var client = CreateDesktopClient(PlayerId);

        var response = await client.GetAsync("/api/tournaments/active-match");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("matchId").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task ActiveMatch_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Version", "0.1.0");

        var response = await client.GetAsync("/api/tournaments/active-match");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T-010: POST /api/matches/{matchId}/player-stats ──────────────────────

    [Fact]
    public async Task PlayerStats_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Version", "0.1.0");

        var response = await client.PostAsJsonAsync($"/api/matches/{Guid.NewGuid()}/player-stats", new
        {
            riotMatchId = "VAL:1",
            kills = 10,
            deaths = 5,
            assists = 3,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PlayerStats_NotParticipantInMatch_Returns403()
    {
        await SeedBaseDataAsync();
        var client = CreateDesktopClient(OtherUserId);

        // Seed a match that OtherUserId is NOT in
        var (matchId, _, _) = await SeedLiveMatchAsync();

        // Seed a Riot account for OtherUserId so the only failure reason is participation
        await SeedRiotAccountAsync(OtherUserId);

        var response = await client.PostAsJsonAsync($"/api/matches/{matchId}/player-stats", new
        {
            riotMatchId = "VALTEST:001",
            kills = 5,
            deaths = 3,
            assists = 2,
            outcome = "win",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PlayerStats_NoRiotAccountLinked_Returns400()
    {
        await SeedBaseDataAsync();

        var (matchId, team1Id, _) = await SeedLiveMatchAsync();

        // PlayerId is in team1 (seeded in SeedLiveMatchAsync)
        var client = CreateDesktopClient(PlayerId);

        var response = await client.PostAsJsonAsync($"/api/matches/{matchId}/player-stats", new
        {
            riotMatchId = "VALTEST:002",
            kills = 12,
            deaths = 4,
            assists = 6,
        });

        // PlayerId has no riot account — expect 400
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private HttpClient CreateDesktopClient(Guid userId)
    {
        var client = _factory.CreateAuthenticatedClient(userId);
        client.DefaultRequestHeaders.Add("X-Client-Version", "0.1.0");
        return client;
    }

    private async Task SeedBaseDataAsync()
    {
        await _seeder.SeedAuthUserAsync(OrganizerId);
        await _seeder.SeedUserRoleAsync(OrganizerId, "organizer");
        await _seeder.SeedAuthUserAsync(PlayerId);
        await _seeder.SeedUserRoleAsync(PlayerId, "casual");
        await _seeder.SeedAuthUserAsync(OtherUserId);
        await _seeder.SeedUserRoleAsync(OtherUserId, "casual");
    }

    private async Task<(Guid MatchId, Guid Team1Id, Guid Team2Id)> SeedLiveMatchAsync()
    {
        var team1Id = await _seeder.SeedTeamAsync(PlayerId, "Team Alpha");
        var team2Id = await _seeder.SeedTeamAsync(OtherUserId, "Team Beta");
        var tournamentId = await _seeder.SeedTournamentAsync(OrganizerId);
        var stageId = await _seeder.SeedStageAsync(tournamentId);
        var versionId = await _seeder.SeedBracketVersionAsync(tournamentId, stageId, "published");
        var matchId = await _seeder.SeedMatchAsync(
            versionId, team1Id: team1Id, team2Id: team2Id, matchStatus: "in_progress");

        return (matchId, team1Id, team2Id);
    }

    private async Task SeedRiotAccountAsync(Guid userId, string? puuid = null)
    {
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO player_riot_accounts (user_id, riot_puuid, game_name, tag_line)
            VALUES (@userId, @puuid, 'TestPlayer', 'NA1')
            ON CONFLICT (user_id) DO NOTHING
            """,
            new { userId, puuid = puuid ?? $"puuid-{userId:N}" });
    }

    private async Task AttachMatchToSessionAsync(Guid sessionId)
    {
        var (matchId, _, _) = await SeedLiveMatchAsync();
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE broadcast_sessions SET match_id = @matchId WHERE id = @sessionId",
            new { matchId, sessionId });
    }
}
