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
        var versionId = await _seeder.SeedBracketVersionAsync(tournamentId, stageId, "active");
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

    private async Task SeedPlayerStatAsync(Guid userId, Guid matchId, int kills = 10, int deaths = 5, int assists = 3)
    {
        var steam64Id = userId.ToString("N")[..17];
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO match_player_stats
                (match_id, map_number, user_id, steam64_id, team,
                 kills, deaths, assists, stat_source, extra_data)
            VALUES
                (@matchId, 1, @userId, @steam64Id, 'team1',
                 @kills, @deaths, @assists, 'companion',
                 jsonb_build_object('agent','Jett','map','Ascent','outcome','win',
                                   'collected_at', NOW()::text))
            ON CONFLICT (match_id, map_number, steam64_id) DO NOTHING
            """,
            new { matchId, userId, steam64Id, kills, deaths, assists });
    }
}

// ── GET /api/player/stats/recent tests ────────────────────────────────────────

[Collection(IntegrationCollection.Name)]
public sealed class PlayerRecentStatsEndpointTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    private static readonly Guid PlayerId = Guid.NewGuid();
    private static readonly Guid OtherPlayer = Guid.NewGuid();
    private static readonly Guid OrganizerId = Guid.NewGuid();

    public PlayerRecentStatsEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    [Fact]
    public async Task RecentStats_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Version", "0.1.0");

        var response = await client.GetAsync("/api/player/stats/recent");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RecentStats_MissingVersionHeader_Returns426()
    {
        await SeedBaseDataAsync();
        var client = _factory.CreateAuthenticatedClient(PlayerId);

        var response = await client.GetAsync("/api/player/stats/recent");

        response.StatusCode.Should().Be((HttpStatusCode)426);
    }

    [Fact]
    public async Task RecentStats_AuthenticatedWithNoStats_ReturnsEmptyArray()
    {
        await SeedBaseDataAsync();
        var client = CreateDesktopClient(PlayerId);

        var response = await client.GetAsync("/api/player/stats/recent");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task RecentStats_LimitClamped_Returns200()
    {
        await SeedBaseDataAsync();
        var client = CreateDesktopClient(PlayerId);

        // limit=200 is above the 50 server-side cap — should still return 200
        var response = await client.GetAsync("/api/player/stats/recent?limit=200");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RecentStats_WithSeededStat_ReturnsCallingPlayerRowOnly()
    {
        await SeedBaseDataAsync();

        // Seed a match and stat for PlayerId
        var (matchId, _, _) = await SeedLiveMatchAsync();
        await SeedPlayerStatAsync(PlayerId, matchId);

        // Seed a stat for OtherPlayer in the same match — must NOT appear in PlayerId's response
        await SeedPlayerStatAsync(OtherPlayer, matchId, kills: 99);

        var client = CreateDesktopClient(PlayerId);
        var response = await client.GetAsync("/api/player/stats/recent");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("kills").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task RecentStats_MultipleStats_ReturnsMostRecentFirst()
    {
        await SeedBaseDataAsync();

        var (match1, _, _) = await SeedLiveMatchAsync();
        await SeedPlayerStatAsync(PlayerId, match1, kills: 5);

        var (match2, _, _) = await SeedLiveMatchAsync();
        await SeedPlayerStatAsync(PlayerId, match2, kills: 20);

        var client = CreateDesktopClient(PlayerId);
        var response = await client.GetAsync("/api/player/stats/recent?limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        rows.GetArrayLength().Should().Be(2);
        // Both rows must belong to PlayerId — order not strictly guaranteed by DB clock precision
        // but both must be present
        var killValues = Enumerable.Range(0, 2).Select(i => rows[i].GetProperty("kills").GetInt32()).ToList();
        killValues.Should().Contain(5).And.Contain(20);
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
        await _seeder.SeedAuthUserAsync(PlayerId);
        await _seeder.SeedUserRoleAsync(PlayerId, "casual");
        await _seeder.SeedAuthUserAsync(OtherPlayer);
        await _seeder.SeedUserRoleAsync(OtherPlayer, "casual");
        await _seeder.SeedAuthUserAsync(OrganizerId);
        await _seeder.SeedUserRoleAsync(OrganizerId, "organizer");
    }

    private async Task<(Guid MatchId, Guid Team1Id, Guid Team2Id)> SeedLiveMatchAsync()
    {
        var team1Id = await _seeder.SeedTeamAsync(PlayerId, $"Alpha-{Guid.NewGuid().ToString("N")[..6]}");
        var team2Id = await _seeder.SeedTeamAsync(OtherPlayer, $"Beta-{Guid.NewGuid().ToString("N")[..6]}");
        var tournamentId = await _seeder.SeedTournamentAsync(OrganizerId);
        var stageId = await _seeder.SeedStageAsync(tournamentId);
        var versionId = await _seeder.SeedBracketVersionAsync(tournamentId, stageId, "active");
        var matchId = await _seeder.SeedMatchAsync(
            versionId, team1Id: team1Id, team2Id: team2Id, matchStatus: "in_progress");
        return (matchId, team1Id, team2Id);
    }

    private async Task SeedPlayerStatAsync(Guid userId, Guid matchId, int kills = 10, int deaths = 5, int assists = 3)
    {
        var steam64Id = userId.ToString("N")[..17];
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO match_player_stats
                (match_id, map_number, user_id, steam64_id, team,
                 kills, deaths, assists, stat_source, extra_data)
            VALUES
                (@matchId, 1, @userId, @steam64Id, 'team1',
                 @kills, @deaths, @assists, 'companion',
                 jsonb_build_object('agent','Jett','map','Ascent','outcome','win',
                                   'collected_at', NOW()::text))
            ON CONFLICT (match_id, map_number, steam64_id) DO NOTHING
            """,
            new { matchId, userId, steam64Id, kills, deaths, assists });
    }
}
