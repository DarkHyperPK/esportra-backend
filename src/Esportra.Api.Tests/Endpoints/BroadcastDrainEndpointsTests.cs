using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

/// <summary>
/// Integration tests for <see cref="Esportra.Api.Endpoints.BroadcastDrainEndpoints"/>
/// (PROJ-059 P4 T-BE-SHARED).
///
/// Covers:
///   - NodeEvent/drain rejects disallowed event_type
///   - NodeEvent/drain rejects payload > 8 KB
///   - NodeEvent/drain rejects non-UUID event_id
///   - Valid event is inserted into broadcast_events
///   - Second call with same event_id is a no-op (idempotency)
///   - Drain endpoint rejects batch > 100
///   - Drain endpoint partial-rejects a mixed valid/invalid batch
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class BroadcastDrainEndpointsTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    // Static GUIDs shared across tests in this class. Each test seeds its own
    // fresh org + show + node to avoid cross-test interference.
    private static readonly Guid OrgOwnerId = Guid.NewGuid();

    public BroadcastDrainEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private HttpClient CreateNodeClient(string rawToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {rawToken}");
        return client;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// Seeds: auth user → organization → broadcast_show (live) → broadcast_node.
    /// Returns (rawToken, orgId, showId, nodeId).
    /// </summary>
    private async Task<(string RawToken, Guid OrgId, Guid ShowId, Guid NodeId)> SeedNodeAndLiveShowAsync()
    {
        var ownerId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(ownerId);
        await _seeder.SeedUserRoleAsync(ownerId, "organizer");

        await using var conn = _seeder.OpenConnection();

        // Org
        var orgId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO public.organizations (id, owner_id, name, slug, created_at, updated_at)
            VALUES (@id, @ownerId, @name, @slug, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = orgId, ownerId, name = $"Org {orgId:N}"[..20], slug = $"org-{orgId:N}"[..20] });

        // Live show (created_by must be a valid auth.users row)
        var showId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO public.broadcast_shows (id, org_id, title, status, created_by, created_at, updated_at)
            VALUES (@id, @orgId, 'Test Show', 'live', @ownerId, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = showId, orgId, ownerId });

        // Node
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashToken(rawToken);
        var nodeId = await conn.QuerySingleAsync<Guid>("""
            INSERT INTO public.broadcast_nodes (org_id, name, token_hash, capabilities, is_active)
            VALUES (@orgId, 'test-node', @tokenHash, '{}'::jsonb, true)
            RETURNING id
            """,
            new { orgId, tokenHash });

        return (rawToken, orgId, showId, nodeId);
    }

    // ── Auth guard ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DrainEvents_Returns401_WhenNoAuthorizationHeader()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/broadcast/events/drain",
            new { events = Array.Empty<object>() });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DrainEvents_Returns401_WhenTokenIsInvalid()
    {
        var client = CreateNodeClient("invalid-token-that-does-not-exist");

        var response = await client.PostAsJsonAsync("/api/broadcast/events/drain",
            new { events = Array.Empty<object>() });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Batch size guard ──────────────────────────────────────────────────────

    [Fact]
    public async Task DrainEvents_Returns400_WhenBatchExceeds100()
    {
        var (rawToken, _, _, _) = await SeedNodeAndLiveShowAsync();
        var client = CreateNodeClient(rawToken);

        // Build 101 syntactically valid events
        var events = Enumerable.Range(0, 101).Select(_ => new
        {
            eventId = Guid.NewGuid().ToString(),
            eventType = "ShowStarted",
            payload = (object?)null,
        }).ToList();

        var response = await client.PostAsJsonAsync("/api/broadcast/events/drain",
            new { events });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Guard (a): event_type allowlist ───────────────────────────────────────

    [Fact]
    public async Task DrainEvents_RejectsAllEvents_WhenEventTypeNotInAllowlist()
    {
        var (rawToken, _, _, _) = await SeedNodeAndLiveShowAsync();
        var client = CreateNodeClient(rawToken);

        var response = await client.PostAsJsonAsync("/api/broadcast/events/drain",
            new
            {
                events = new[]
                {
                    new { eventId = Guid.NewGuid().ToString(), eventType = "GepStats", payload = (object?)null },
                    new { eventId = Guid.NewGuid().ToString(), eventType = "ArbitraryEvent", payload = (object?)null },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("accepted").GetInt32().Should().Be(0);
        body.GetProperty("rejected").GetInt32().Should().Be(2);
    }

    // ── Guard (c): event_id UUID format ───────────────────────────────────────

    [Fact]
    public async Task DrainEvents_RejectsEvent_WhenEventIdIsNotUuid()
    {
        var (rawToken, _, _, _) = await SeedNodeAndLiveShowAsync();
        var client = CreateNodeClient(rawToken);

        var response = await client.PostAsJsonAsync("/api/broadcast/events/drain",
            new
            {
                events = new[]
                {
                    new { eventId = "not-a-uuid", eventType = "ShowStarted", payload = (object?)null },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("accepted").GetInt32().Should().Be(0);
        body.GetProperty("rejected").GetInt32().Should().Be(1);
    }

    // ── Guard (b): payload size cap ───────────────────────────────────────────

    [Fact]
    public async Task DrainEvents_RejectsEvent_WhenPayloadExceeds8Kb()
    {
        var (rawToken, _, _, _) = await SeedNodeAndLiveShowAsync();
        var client = CreateNodeClient(rawToken);

        // JSON-serialized data field > 8192 bytes
        var largeData = new { data = new string('x', 9000) };

        var response = await client.PostAsJsonAsync("/api/broadcast/events/drain",
            new
            {
                events = new[]
                {
                    new
                    {
                        eventId = Guid.NewGuid().ToString(),
                        eventType = "ShowStarted",
                        payload = (object?)largeData,
                    },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("accepted").GetInt32().Should().Be(0);
        body.GetProperty("rejected").GetInt32().Should().Be(1);
    }

    // ── Valid event insert + idempotency ──────────────────────────────────────

    [Fact]
    public async Task DrainEvents_InsertsEvent_WhenPayloadIsValid()
    {
        var (rawToken, _, showId, nodeId) = await SeedNodeAndLiveShowAsync();
        var client = CreateNodeClient(rawToken);

        var eventId = Guid.NewGuid().ToString();

        var response = await client.PostAsJsonAsync("/api/broadcast/events/drain",
            new
            {
                events = new[]
                {
                    new { eventId, eventType = "ShowStarted", payload = (object?)null },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("accepted").GetInt32().Should().Be(1);
        body.GetProperty("rejected").GetInt32().Should().Be(0);

        // Verify the row was written
        await using var conn = _seeder.OpenConnection();
        dynamic? row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT event_id, event_type, source FROM broadcast_events WHERE event_id = @eventId",
            new { eventId });

        ((object?)row).Should().NotBeNull();
        ((string)row!.event_type).Should().Be("ShowStarted");
        ((string)row.source).Should().Be("node");
    }

    [Fact]
    public async Task DrainEvents_IsIdempotent_WhenSameEventIdSentTwice()
    {
        var (rawToken, _, _, _) = await SeedNodeAndLiveShowAsync();
        var client = CreateNodeClient(rawToken);

        var eventId = Guid.NewGuid().ToString();
        var body = new
        {
            events = new[]
            {
                new { eventId, eventType = "ShowEnded", payload = (object?)null },
            },
        };

        // First call
        var r1 = await client.PostAsJsonAsync("/api/broadcast/events/drain", body);
        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        var b1 = await r1.Content.ReadFromJsonAsync<JsonElement>();
        b1.GetProperty("accepted").GetInt32().Should().Be(1);

        // Second call — same event_id
        var r2 = await client.PostAsJsonAsync("/api/broadcast/events/drain", body);
        r2.StatusCode.Should().Be(HttpStatusCode.OK);
        var b2 = await r2.Content.ReadFromJsonAsync<JsonElement>();
        // ON CONFLICT DO NOTHING — still reports accepted=1 (no error, no duplicate)
        b2.GetProperty("accepted").GetInt32().Should().Be(1);

        // DB must have exactly one row for this event_id
        await using var conn = _seeder.OpenConnection();
        var count = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM broadcast_events WHERE event_id = @eventId",
            new { eventId });
        count.Should().Be(1);
    }

    // ── Mixed valid / invalid batch ───────────────────────────────────────────

    [Fact]
    public async Task DrainEvents_PartiallyRejects_WhenBatchContainsMixedEvents()
    {
        var (rawToken, _, _, _) = await SeedNodeAndLiveShowAsync();
        var client = CreateNodeClient(rawToken);

        var goodEventId1 = Guid.NewGuid().ToString();
        var goodEventId2 = Guid.NewGuid().ToString();

        var response = await client.PostAsJsonAsync("/api/broadcast/events/drain",
            new
            {
                events = new object[]
                {
                    new { eventId = goodEventId1, eventType = "ShowStarted", payload = (object?)null },
                    new { eventId = "not-a-uuid",  eventType = "ShowStarted", payload = (object?)null }, // bad event_id
                    new { eventId = goodEventId2, eventType = "ShowEnded",   payload = (object?)null },
                    new { eventId = Guid.NewGuid().ToString(), eventType = "DisallowedType", payload = (object?)null }, // bad type
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("accepted").GetInt32().Should().Be(2);
        body.GetProperty("rejected").GetInt32().Should().Be(2);
    }

    // ── Corrections endpoint ──────────────────────────────────────────────────

    [Fact]
    public async Task DrainCorrections_Returns401_WhenNoAuthorizationHeader()
    {
        var showId = Guid.NewGuid();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/broadcast/shows/{showId}/corrections",
            new { corrections = Array.Empty<object>() });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DrainCorrections_Returns404_WhenShowDoesNotBelongToOrg()
    {
        var (rawToken, _, _, _) = await SeedNodeAndLiveShowAsync();
        var client = CreateNodeClient(rawToken);

        // Use a random showId not owned by this node's org
        var unknownShowId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(
            $"/api/broadcast/shows/{unknownShowId}/corrections",
            new
            {
                corrections = new[]
                {
                    new { matchCode = "GF1", mapIndex = 0, teamAScore = 13, teamBScore = 7 },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DrainCorrections_InsertsCorrections_WhenNodeOwnsShow()
    {
        var (rawToken, orgId, showId, nodeId) = await SeedNodeAndLiveShowAsync();
        var client = CreateNodeClient(rawToken);

        var response = await client.PostAsJsonAsync(
            $"/api/broadcast/shows/{showId}/corrections",
            new
            {
                corrections = new[]
                {
                    new
                    {
                        matchCode = "GF1",
                        mapIndex = 1,
                        teamAScore = 13,
                        teamBScore = 5,
                        reason = "Score updated via FIX_DATA",
                    },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("inserted").GetInt32().Should().Be(1);

        // Verify row in DB
        await using var conn = _seeder.OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT correction_type, source, node_id, after_state
            FROM broadcast_corrections
            WHERE show_id = @showId AND node_id = @nodeId
            ORDER BY created_at DESC LIMIT 1
            """,
            new { showId, nodeId });

        ((object?)row).Should().NotBeNull();
        ((string)row!.correction_type).Should().Be("fix_data");
        ((string)row.source).Should().Be("node");
        ((Guid)row.node_id).Should().Be(nodeId);
    }
}
