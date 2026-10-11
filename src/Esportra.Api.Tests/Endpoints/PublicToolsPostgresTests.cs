using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.ScheduledJobs;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Migrations;
using FluentAssertions;
using Hangfire.Common;
using Hangfire.States;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

/// <summary>Opt-in real PostgreSQL coverage in a fresh database on a disposable loopback server.</summary>
public sealed class PublicToolsPostgresTests
{
    [PublicToolsPostgresFact]
    public async Task Migration_IsReplayableNullableWithoutBackfillOrPolicyChanges()
    {
        await using var fixture = await PublicToolsPostgresFixture.CreateAsync();
        await using var conn = fixture.OpenConnection();
        var column = await conn.QuerySingleAsync<(string DataType, string IsNullable)>(
            "SELECT data_type, is_nullable FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'public_veto_sessions' AND column_name = 'overlay_first_loaded_at'");
        column.Should().Be(("timestamp with time zone", "YES"));
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM public.public_veto_sessions WHERE overlay_first_loaded_at IS NOT NULL")).Should().Be(0);
        (await conn.ExecuteScalarAsync<bool>("SELECT relrowsecurity FROM pg_class WHERE oid = 'public.public_veto_sessions'::regclass")).Should().BeTrue();
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM pg_policies WHERE tablename = 'public_veto_sessions'")).Should().Be(1);
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM public.public_veto_sessions")).Should().Be(62);
    }

    [PublicToolsPostgresFact]
    public async Task List_UsesRealSqlForStablePaginationAllFilteredCountsAndNullableTimestamps()
    {
        await using var fixture = await PublicToolsPostgresFixture.CreateAsync();
        await using var host = await PublicToolsTestHost.CreateAsync(fixture);
        var first = await (await host.GetAsync("/api/admin/public-tools/veto-sessions")).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await host.GetAsync("/api/admin/public-tools/veto-sessions?page=2")).Content.ReadFromJsonAsync<JsonElement>();
        first.GetProperty("sessions").GetArrayLength().Should().Be(50);
        second.GetProperty("sessions").GetArrayLength().Should().Be(12);
        first.GetProperty("total").GetInt64().Should().Be(62);
        first.GetProperty("summary").GetProperty("completedSessions").GetInt64().Should().Be(30);
        first.GetProperty("summary").GetProperty("overlaySessions").GetInt64().Should().Be(0);
        first.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("id").GetGuid())
            .Intersect(second.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("id").GetGuid())).Should().BeEmpty();
        await using var conn = fixture.OpenConnection();
        var expected = (await conn.QueryAsync<Guid>("SELECT id FROM public.public_veto_sessions ORDER BY created_at DESC, id DESC LIMIT 50")).ToArray();
        first.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).Should().Equal(expected);

        foreach (var game in new[] { "valorant", "cs2", "r6s" })
        {
            var filtered = await (await host.GetAsync("/api/admin/public-tools/veto-sessions?game=" + game)).Content.ReadFromJsonAsync<JsonElement>();
            filtered.GetProperty("sessions").EnumerateArray().Should().OnlyContain(s => s.GetProperty("game").GetString() == game);
            var count = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM public.public_veto_sessions WHERE game = @game", new { game });
            filtered.GetProperty("summary").GetProperty("totalSessions").GetInt64().Should().Be(count);
        }
    }

    [PublicToolsPostgresFact]
    public async Task Overlay_QueuesOnlyValidStateLoadsAndStopsAfterFirstRecordedLoad()
    {
        await using var fixture = await PublicToolsPostgresFixture.CreateAsync();
        await using var host = await PublicToolsTestHost.CreateAsync(fixture);
        foreach (var path in new[] { "overlay/missing", "overlay/expired-overlay", "overlay/host-1", "overlay/team1-1" })
            (await host.GetAsync("/api/tools/map-veto/" + path, null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        foreach (var path in new[] { "overlay/overlay-1/history", "overlay-1/history", "host/host-1", "team/team1-1" })
            (await host.GetAsync("/api/tools/map-veto/" + path, null)).StatusCode.Should().Be(HttpStatusCode.OK);
        host.Jobs.DidNotReceiveWithAnyArgs().Create(default!, default!);

        var before = DateTime.UtcNow;
        (await host.GetAsync("/api/tools/map-veto/overlay/overlay-1", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var queued = host.Jobs.ReceivedCalls().Single(c => c.GetMethodInfo().Name == "Create").GetArguments()[0] as Job;
        queued.Should().NotBeNull();
        queued!.Args[0].Should().Be(fixture.ActiveSessionId);
        var loadedAt = (DateTime)queued.Args[1];
        loadedAt.Should().BeOnOrAfter(before);
        await new PublicVetoOverlayUsageJob(fixture).RecordAsync(fixture.ActiveSessionId, loadedAt, CancellationToken.None);
        (await host.GetAsync("/api/tools/map-veto/overlay/overlay-1", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        host.Jobs.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
        var body = await (await host.GetAsync("/api/admin/public-tools/veto-sessions?game=cs2")).Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("summary").GetProperty("overlaySessions").GetInt64().Should().Be(1);
        body.GetProperty("sessions").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == fixture.ActiveSessionId)
            .GetProperty("overlayAccessedAt").GetDateTime().Should().BeCloseTo(loadedAt, TimeSpan.FromMilliseconds(1));
    }

    [PublicToolsPostgresFact]
    public async Task UsageJob_PreservesFirstTimestampUnderConcurrentWritesAndRetries()
    {
        await using var fixture = await PublicToolsPostgresFixture.CreateAsync();
        var initial = DateTime.UtcNow.AddSeconds(-10);
        await new PublicVetoOverlayUsageJob(fixture).RecordAsync(fixture.ActiveSessionId, initial, CancellationToken.None);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            new PublicVetoOverlayUsageJob(fixture).RecordAsync(fixture.ActiveSessionId, initial.AddSeconds(i + 1), CancellationToken.None)));
        await using var conn = fixture.OpenConnection();
        var recorded = await conn.ExecuteScalarAsync<DateTime>("SELECT overlay_first_loaded_at FROM public.public_veto_sessions WHERE id = @id", new { id = fixture.ActiveSessionId });
        recorded.Should().BeCloseTo(initial, TimeSpan.FromMilliseconds(1));

        await conn.ExecuteAsync("UPDATE public.public_veto_sessions SET overlay_first_loaded_at = NULL WHERE id = @id", new { id = fixture.ActiveSessionId });
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            new PublicVetoOverlayUsageJob(fixture).RecordAsync(fixture.ActiveSessionId, initial, CancellationToken.None)));
        var raced = await conn.ExecuteScalarAsync<DateTime>("SELECT overlay_first_loaded_at FROM public.public_veto_sessions WHERE id = @id", new { id = fixture.ActiveSessionId });
        raced.Should().BeCloseTo(initial, TimeSpan.FromMilliseconds(1));
    }
}

internal sealed class PublicToolsPostgresFactAttribute : FactAttribute
{
    public PublicToolsPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROJ060_POSTGRES_TEST_CONNECTION")))
            Skip = "Set PROJ060_POSTGRES_TEST_CONNECTION to the disposable loopback PostgreSQL server to run real SQL tests.";
    }
}

internal sealed class PublicToolsPostgresFixture : IDbConnectionFactory, IAsyncDisposable
{
    private readonly string _adminConnection;
    private readonly string _databaseName = "esportra_proj060_tests_" + Guid.NewGuid().ToString("N");
    private string _connectionString = "";
    public Guid ActiveSessionId { get; } = Guid.NewGuid();

    private PublicToolsPostgresFixture(string adminConnection) => _adminConnection = adminConnection;

    public static async Task<PublicToolsPostgresFixture> CreateAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PROJ060_POSTGRES_TEST_CONNECTION"));
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1") || builder.Port == 5432)
            throw new InvalidOperationException("PROJ-060 tests require a disposable loopback server on a dedicated non-default port.");
        var fixture = new PublicToolsPostgresFixture(builder.ConnectionString);
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {fixture._databaseName}");
        builder.Database = fixture._databaseName;
        fixture._connectionString = builder.ConnectionString;
        try { await fixture.InitializeAsync(); }
        catch { await fixture.DisposeAsync(); throw; }
        return fixture;
    }

    public NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public System.Data.IDbConnection CreateConnection() => OpenConnection();

    private const string SchemaAndSeedSql = """
            CREATE TABLE public.public_veto_sessions (
                id uuid PRIMARY KEY, game text NOT NULL, best_of integer NOT NULL DEFAULT 1,
                team1_name text NOT NULL DEFAULT 'Alpha', team2_name text NOT NULL DEFAULT 'Beta',
                team1_id uuid NOT NULL DEFAULT gen_random_uuid(), team2_id uuid NOT NULL DEFAULT gen_random_uuid(),
                status text NOT NULL DEFAULT 'in_progress',
                selected_map_pool text[] NOT NULL DEFAULT ARRAY['m1','m2','m3','m4','m5','m6','m7'],
                current_team_id uuid, current_action text, current_action_number integer NOT NULL DEFAULT 1,
                team1_banned_maps text[] NOT NULL DEFAULT '{}', team2_banned_maps text[] NOT NULL DEFAULT '{}',
                team1_picked_maps jsonb NOT NULL DEFAULT '[]', team2_picked_maps jsonb NOT NULL DEFAULT '[]',
                selected_map_id text, host_token text UNIQUE, team1_token text UNIQUE, team2_token text UNIQUE, overlay_token text UNIQUE,
                toss_enabled boolean NOT NULL DEFAULT false, custom_sequence jsonb,
                created_at timestamptz NOT NULL, expires_at timestamptz NOT NULL, started_at timestamptz, completed_at timestamptz,
                toss_winner_team_id uuid, toss_completed_at timestamptz, toss_first_actor_team_id uuid
            );
            ALTER TABLE public.public_veto_sessions ENABLE ROW LEVEL SECURITY;
            CREATE POLICY test_existing_policy ON public.public_veto_sessions FOR SELECT USING (false);
            CREATE TABLE public.game_maps (id text PRIMARY KEY, game text, map_name text, map_image_url text, is_active boolean);
            CREATE TABLE public.public_veto_actions (session_id uuid, action_number integer, team_side text, team_id uuid, action_type text, map_id text, side text, created_at timestamptz);
            INSERT INTO public.public_veto_sessions (id, game, status, created_at, expires_at, completed_at, host_token, team1_token, team2_token, overlay_token)
            SELECT CASE WHEN n = 1 THEN @activeId ELSE gen_random_uuid() END,
                   CASE n % 3 WHEN 0 THEN 'valorant' WHEN 1 THEN 'cs2' ELSE 'r6s' END,
                   CASE n % 2 WHEN 0 THEN 'completed' ELSE 'in_progress' END,
                   '2026-10-01'::timestamptz + (n / 2) * interval '1 minute', now() + interval '1 day',
                   CASE n % 2 WHEN 0 THEN now() ELSE NULL END,
                   'host-' || n, 'team1-' || n, 'team2-' || n, 'overlay-' || n
            FROM generate_series(1, 61) n;
            INSERT INTO public.public_veto_sessions (id, game, created_at, expires_at, overlay_token)
            VALUES (gen_random_uuid(), 'valorant', now(), now() - interval '1 hour', 'expired-overlay');
            """;

    private async Task InitializeAsync()
    {
        await using var conn = OpenConnection();
        await conn.ExecuteAsync(SchemaAndSeedSql, new { activeId = ActiveSessionId });
        var assembly = typeof(MigrationRunner).Assembly;
        var migrationName = assembly.GetManifestResourceNames().Single(n => n.EndsWith("20261011120002_public_veto_overlay_usage.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(migrationName)!;
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync();
        await conn.ExecuteAsync(sql);
        await conn.ExecuteAsync(sql);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_adminConnection);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
    }
}
