using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

/// <summary>
/// Integration tests for <see cref="Esportra.Api.Endpoints.BroadcastReportEndpoints"/>
/// (PROJ-059 P4 T-BE-T38).
///
/// Covers:
///   GET /api/broadcast/shows/{showId}/report:
///   - Unauthenticated → 401
///   - Non-org-owner → 403
///   - Show not ended → 409
///   - All sections present for an ended show with no data
///   - durationSeconds computed from startedAt / endedAt
///   - players section populated from match_player_stats
///   - map source = 'captain' when no corrections or broadcast events
///   - map source = 'gep'    when gep_conflict correction exists (no override)
///   - map source = 'manual' when gep_conflict_override correction exists
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class BroadcastReportEndpointsTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    public BroadcastReportEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    // ── Seeding helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds: auth user (with profile) → organization → ended broadcast_show.
    /// Returns (ownerId, orgId, showId).
    /// </summary>
    private async Task<(Guid OwnerId, Guid OrgId, Guid ShowId)> SeedEndedShowAsync(
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null)
    {
        var ownerId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(ownerId);
        await _seeder.SeedUserRoleAsync(ownerId, "organizer");

        await using var conn = _seeder.OpenConnection();

        var orgId = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            INSERT INTO public.organizations (id, owner_id, name, slug, created_at, updated_at)
            VALUES (@id, @ownerId, @name, @slug, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = orgId, ownerId, name = $"Org {orgId:N}"[..20], slug = $"rp-{orgId:N}"[..20] });

        var showId = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            INSERT INTO public.broadcast_shows
                (id, org_id, title, status, started_at, ended_at, created_by, created_at, updated_at)
            VALUES
                (@id, @orgId, 'Test Show', 'ended', @startedAt, @endedAt, @ownerId, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = showId, orgId, ownerId, startedAt, endedAt });

        return (ownerId, orgId, showId);
    }

    /// <summary>
    /// Seeds a full match chain (tournament → stage → version → match),
    /// attaches the match to the show via broadcast_show_matches,
    /// and inserts one completed brkt_match_game row.
    /// Returns (ownerId, showId, matchId, gameId).
    /// </summary>
    private async Task<(Guid OwnerId, Guid ShowId, Guid MatchId, Guid GameId)>
        SeedShowWithMatchGameAsync()
    {
        var (ownerId, _, showId) = await SeedEndedShowAsync();

        var tournamentId = await _seeder.SeedTournamentAsync(ownerId);
        var stageId = await _seeder.SeedStageAsync(tournamentId);
        var versionId = await _seeder.SeedBracketVersionAsync(tournamentId, stageId, "published");
        var matchId = await _seeder.SeedMatchAsync(versionId, matchStatus: "completed");

        await using var conn = _seeder.OpenConnection();

        // Attach match to show
        await conn.ExecuteAsync(
            """
            INSERT INTO public.broadcast_show_matches
                (show_id, match_id, display_order, status, match_code)
            VALUES (@showId, @matchId, 1, 'completed', 'MAIN')
            ON CONFLICT DO NOTHING
            """,
            new { showId, matchId });

        // Insert one completed game
        var gameId = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.brkt_match_games
                (match_id, game_number, team1_score, team2_score, status, completed_at)
            VALUES (@matchId, 1, 13, 7, 'completed', NOW())
            RETURNING id
            """,
            new { matchId });

        return (ownerId, showId, matchId, gameId);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetReport_Returns401_WhenUnauthenticated()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/broadcast/shows/{Guid.NewGuid()}/report");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetReport_Returns403_WhenNotOrgOwner()
    {
        var (_, _, showId) = await SeedEndedShowAsync();

        var nonOwnerId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(nonOwnerId);

        var client = _factory.CreateAuthenticatedClient(nonOwnerId);
        var response = await client.GetAsync($"/api/broadcast/shows/{showId}/report");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetReport_Returns409_WhenShowNotEnded()
    {
        var ownerId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(ownerId);
        await _seeder.SeedUserRoleAsync(ownerId, "organizer");

        await using var conn = _seeder.OpenConnection();

        var orgId = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            INSERT INTO public.organizations (id, owner_id, name, slug, created_at, updated_at)
            VALUES (@id, @ownerId, @name, @slug, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = orgId, ownerId, name = $"Org {orgId:N}"[..20], slug = $"rp2-{orgId:N}"[..20] });

        var showId = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            INSERT INTO public.broadcast_shows
                (id, org_id, title, status, created_by, created_at, updated_at)
            VALUES (@id, @orgId, 'Live Show', 'live', @ownerId, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = showId, orgId, ownerId });

        var client = _factory.CreateAuthenticatedClient(ownerId);
        var response = await client.GetAsync($"/api/broadcast/shows/{showId}/report");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("Show has not ended");
    }

    [Fact]
    public async Task GetReport_ReturnsAllSections_ForEndedShowWithNoData()
    {
        var (ownerId, _, showId) = await SeedEndedShowAsync();

        var client = _factory.CreateAuthenticatedClient(ownerId);
        var response = await client.GetAsync($"/api/broadcast/shows/{showId}/report");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.TryGetProperty("show", out _).Should().BeTrue("show section must be present");
        body.TryGetProperty("matches", out _).Should().BeTrue("matches section must be present");
        body.TryGetProperty("crew", out _).Should().BeTrue("crew section must be present");
        body.TryGetProperty("sponsors", out _).Should().BeTrue("sponsors section must be present");
        body.TryGetProperty("chapters", out _).Should().BeTrue("chapters section must be present");
        body.TryGetProperty("corrections", out _).Should().BeTrue("corrections section must be present");
        body.TryGetProperty("players", out _).Should().BeTrue("players section must be present");

        body.GetProperty("corrections").GetProperty("count").GetInt64().Should().Be(0);
        body.GetProperty("matches").GetArrayLength().Should().Be(0);
        body.GetProperty("crew").GetArrayLength().Should().Be(0);
        body.GetProperty("sponsors").GetArrayLength().Should().Be(0);
        body.GetProperty("chapters").GetArrayLength().Should().Be(0);
        body.GetProperty("players").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetReport_DurationSeconds_ComputedFromStartedAtToEndedAt()
    {
        var startedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var endedAt = startedAt.AddSeconds(7200);
        var (ownerId, _, showId) = await SeedEndedShowAsync(startedAt, endedAt);

        var client = _factory.CreateAuthenticatedClient(ownerId);
        var response = await client.GetAsync($"/api/broadcast/shows/{showId}/report");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("show").GetProperty("durationSeconds").GetInt32().Should().Be(7200);
    }

    [Fact]
    public async Task GetReport_MapSource_IsCaptain_WhenNoCorrectionsOrBroadcastEvents()
    {
        var (ownerId, showId, _, _) = await SeedShowWithMatchGameAsync();

        var client = _factory.CreateAuthenticatedClient(ownerId);
        var response = await client.GetAsync($"/api/broadcast/shows/{showId}/report");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var firstMatch = body.GetProperty("matches").EnumerateArray().First();
        firstMatch.GetProperty("maps").GetArrayLength().Should().Be(1);

        var firstMap = firstMatch.GetProperty("maps").EnumerateArray().First();
        firstMap.GetProperty("source").GetString().Should().Be("captain");
        firstMap.GetProperty("mapNumber").GetInt32().Should().Be(1);
        firstMap.GetProperty("teamAScore").GetInt32().Should().Be(13);
        firstMap.GetProperty("teamBScore").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task GetReport_MapSource_IsGep_WhenGepConflictCorrectionExists()
    {
        var (ownerId, showId, _, gameId) = await SeedShowWithMatchGameAsync();

        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO public.broadcast_corrections
                (show_id, correction_type, target_id, target_type, after_state, source, created_at)
            VALUES
                (@showId, 'gep_conflict', @gameId, 'brkt_match_game', '{}'::jsonb, 'broadcast', NOW())
            """,
            new { showId, gameId });

        var client = _factory.CreateAuthenticatedClient(ownerId);
        var response = await client.GetAsync($"/api/broadcast/shows/{showId}/report");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var firstMap = body.GetProperty("matches").EnumerateArray().First()
            .GetProperty("maps").EnumerateArray().First();
        firstMap.GetProperty("source").GetString().Should().Be("gep");
    }

    [Fact]
    public async Task GetReport_MapSource_IsManual_WhenGepConflictOverrideCorrectionExists()
    {
        var (ownerId, showId, _, gameId) = await SeedShowWithMatchGameAsync();

        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO public.broadcast_corrections
                (show_id, correction_type, target_id, target_type, after_state, source, created_at)
            VALUES
                (@showId, 'gep_conflict_override', @gameId, 'brkt_match_game', '{}'::jsonb, 'broadcast', NOW())
            """,
            new { showId, gameId });

        var client = _factory.CreateAuthenticatedClient(ownerId);
        var response = await client.GetAsync($"/api/broadcast/shows/{showId}/report");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var firstMap = body.GetProperty("matches").EnumerateArray().First()
            .GetProperty("maps").EnumerateArray().First();
        firstMap.GetProperty("source").GetString().Should().Be("manual");
    }

    [Fact]
    public async Task GetReport_Players_ContainsKdaFromMatchPlayerStats()
    {
        var (ownerId, showId, matchId, _) = await SeedShowWithMatchGameAsync();

        // Seed a player with stats
        var playerId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(playerId);

        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO public.match_player_stats
                (match_id, map_number, user_id, stat_source, kills, deaths, assists, created_at)
            VALUES (@matchId, 1, @playerId, 'companion', 18, 5, 3, NOW())
            ON CONFLICT DO NOTHING
            """,
            new { matchId, playerId });

        var client = _factory.CreateAuthenticatedClient(ownerId);
        var response = await client.GetAsync($"/api/broadcast/shows/{showId}/report");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var players = body.GetProperty("players").EnumerateArray().ToArray();
        players.Should().HaveCount(1);

        var player = players[0];
        player.GetProperty("matchId").GetString().Should().Be(matchId.ToString());
        player.GetProperty("kills").GetInt64().Should().Be(18);
        player.GetProperty("deaths").GetInt64().Should().Be(5);
        player.GetProperty("assists").GetInt64().Should().Be(3);
    }
}
