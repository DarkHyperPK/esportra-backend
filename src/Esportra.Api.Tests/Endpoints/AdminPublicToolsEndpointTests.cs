using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Esportra.Api.Endpoints;
using Esportra.Api.ScheduledJobs;
using Esportra.Api.Tests.Infrastructure;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed class AdminPublicToolsEndpointTests
{
    [Fact]
    public async Task BracketCompatibility_PreviewRespondsAndAllExistingApiRoutesRemainMapped()
    {
        await using var host = await PublicToolsTestHost.CreateAsync();
        var preview = await host.PostJsonAsync("/api/tools/brackets/preview", new
        {
            title = "Compatibility smoke",
            format = "single_elimination",
            teams = new[] { "Alpha", "Bravo" },
            bestOf = 3,
            bracketSize = 2,
        }, identity: null);

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("Compatibility smoke");
        body.GetProperty("teams").GetArrayLength().Should().Be(2);
        host.BracketRoutes.Should().BeEquivalentTo(
            "POST /api/tools/brackets/preview",
            "POST /api/tools/brackets",
            "GET /api/tools/brackets/mine",
            "GET /api/tools/brackets/{id:guid}",
            "GET /api/tools/brackets/share/{token}",
            "PATCH /api/tools/brackets/{id:guid}/matches/{matchId:guid}",
            "POST /api/tools/brackets/{id:guid}/reset",
            "PATCH /api/tools/brackets/{id:guid}/sharing",
            "DELETE /api/tools/brackets/{id:guid}");
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("member", HttpStatusCode.Forbidden)]
    [InlineData("admin", HttpStatusCode.Forbidden)]
    [InlineData("missing-context", HttpStatusCode.Forbidden)]
    public async Task List_RejectsRequestsWithoutServerSuperAdminContext(string? identity, HttpStatusCode expected)
    {
        await using var host = await PublicToolsTestHost.CreateAsync();
        var response = await host.GetAsync("/api/admin/public-tools/veto-sessions", identity);
        response.StatusCode.Should().Be(expected);
        host.Db.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?page=-1")]
    [InlineData("?page=2147483647")]
    [InlineData("?page=abc")]
    [InlineData("?game=unknown")]
    [InlineData("?game=VALORANT")]
    [InlineData("?game=")]
    public async Task List_Returns400BeforeDatabase_WhenFilterOrPageIsInvalid(string query)
    {
        await using var host = await PublicToolsTestHost.CreateAsync();
        (await host.GetAsync("/api/admin/public-tools/veto-sessions" + query)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Db.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("valorant")]
    [InlineData("cs2")]
    [InlineData("r6s")]
    public async Task List_ReturnsCamelCaseTokenFreeSessionsAndFullFilteredSummary(string? game)
    {
        await using var host = await PublicToolsTestHost.CreateAsync();
        var now = DateTime.UtcNow;
        host.Db.Results.Enqueue(Table(["TotalSessions", "CompletedSessions", "OverlaySessions"], [typeof(long), typeof(long), typeof(long)], [123L, 65L, 90L]));
        host.Db.Results.Enqueue(Table(
            ["Id", "Game", "BestOf", "Team1Name", "Team2Name", "Status", "CreatedAt", "ExpiresAt", "CompletedAt", "OverlayAccessedAt"],
            [typeof(Guid), typeof(string), typeof(int), typeof(string), typeof(string), typeof(string), typeof(DateTime), typeof(DateTime), typeof(DateTime), typeof(DateTime)],
            [Guid.NewGuid(), game ?? "valorant", 3, "Alpha", "Beta", "in_progress", now, now.AddDays(1), null, null]));

        var response = await host.GetAsync("/api/admin/public-tools/veto-sessions?page=2" + (game is null ? "" : "&game=" + game));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("page").GetInt32().Should().Be(2);
        body.GetProperty("pageSize").GetInt32().Should().Be(50);
        body.GetProperty("total").GetInt64().Should().Be(123);
        var session = body.GetProperty("sessions")[0];
        session.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "game", "bestOf", "team1Name", "team2Name", "status", "createdAt", "expiresAt", "completedAt", "overlayAccessedAt");
        session.GetProperty("overlayAccessedAt").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("summary").GetProperty("completedSessions").GetInt64().Should().Be(65);
        body.GetProperty("summary").GetProperty("overlaySessions").GetInt64().Should().Be(90);
        host.Db.Commands[1].CommandText.Should().Contain("ORDER BY created_at DESC, id DESC").And.NotContain("SELECT *").And.NotContain("token");
        host.Db.Commands[1].Parameters["offset"].Should().Be(50);
        host.Db.Commands[1].Parameters["pageSize"].Should().Be(50);
        host.Db.Commands.Should().OnlyContain(c => Equals(c.Parameters["game"], game));
        host.Db.Commands[0].CommandText.Should().NotContain("LIMIT").And.NotContain("OFFSET");
    }

    [Fact]
    public async Task List_ReturnsFullCountsEvenOnAnEmptyLaterPage()
    {
        await using var host = await PublicToolsTestHost.CreateAsync();
        host.Db.Results.Enqueue(Table(["TotalSessions", "CompletedSessions", "OverlaySessions"], [typeof(long), typeof(long), typeof(long)], [1L, 1L, 0L]));
        host.Db.Results.Enqueue(new DataTable());
        var body = await (await host.GetAsync("/api/admin/public-tools/veto-sessions?page=2")).Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("sessions").GetArrayLength().Should().Be(0);
        body.GetProperty("total").GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task Overlay_Returns404WithoutQueueing_WhenTokenLookupFindsNoValidSession()
    {
        await using var host = await PublicToolsTestHost.CreateAsync();
        host.Db.Results.Enqueue(new DataTable());
        (await host.GetAsync("/api/tools/map-veto/overlay/invalid", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        host.Jobs.DidNotReceiveWithAnyArgs().Create(default!, default!);
        host.Db.Commands.Single().CommandText.Should().Contain("overlay_token = @token").And.Contain("expires_at > now()");
    }

    [Fact]
    public async Task UsageJob_ExecutesAnAtomicNullOnlyUpdateWithCapturedLoadTime()
    {
        var db = new RecordingPublicToolsDb();
        var job = new PublicVetoOverlayUsageJob(db);
        var id = Guid.NewGuid();
        var loadedAt = DateTime.UtcNow;
        await job.RecordAsync(id, loadedAt, CancellationToken.None);
        await job.RecordAsync(id, loadedAt.AddSeconds(3), CancellationToken.None);
        db.Commands.Should().HaveCount(2);
        db.Commands.Should().OnlyContain(c => c.CommandText.Contains("overlay_first_loaded_at IS NULL"));
        db.Commands[0].Parameters["sessionId"].Should().Be(id);
        db.Commands[0].Parameters["loadedAt"].Should().Be(loadedAt);
    }

    [Fact]
    public void UsageScheduler_QueuesOnlyScalarIdentifiersAndObservesQueueFailures()
    {
        var jobs = Substitute.For<IBackgroundJobClient>();
        var logger = new CapturingLogger<PublicVetoOverlayUsageScheduler>();
        var scheduler = new PublicVetoOverlayUsageScheduler(jobs, logger);
        var id = Guid.NewGuid();
        var loadedAt = DateTime.UtcNow;
        scheduler.Enqueue(id, loadedAt);
        jobs.Received().Create(Arg.Is<Job>(j => j.Type == typeof(PublicVetoOverlayUsageJob) && Equals(j.Args[0], id) && Equals(j.Args[1], loadedAt)), Arg.Any<IState>());
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(_ => throw new InvalidOperationException("storage unavailable"));
        scheduler.Invoking(s => s.Enqueue(id, loadedAt)).Should().NotThrow();
        logger.Messages.Should().ContainSingle(m => m.Contains("Could not queue"));
    }

    [Fact]
    public async Task UsageJob_DoesNotOpenDatabase_WhenCancelled()
    {
        var db = Substitute.For<IDbConnectionFactory>();
        var job = new PublicVetoOverlayUsageJob(db);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await job.Invoking(j => j.RecordAsync(Guid.NewGuid(), DateTime.UtcNow, cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        db.DidNotReceive().CreateConnection();
    }

    [Fact]
    public async Task UsageJob_PropagatesDatabaseFailuresForManagedRetries()
    {
        var db = Substitute.For<IDbConnectionFactory>();
        db.CreateConnection().Returns(_ => throw new InvalidOperationException("unavailable"));
        var job = new PublicVetoOverlayUsageJob(db);
        await job.Invoking(j => j.RecordAsync(Guid.NewGuid(), DateTime.UtcNow, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    internal static DataTable Table(string[] names, Type[] types, params object?[][] rows)
    {
        var table = new DataTable();
        for (var i = 0; i < names.Length; i++) table.Columns.Add(names[i], types[i]);
        foreach (var row in rows) table.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());
        return table;
    }
}

internal sealed class PublicToolsTestHost(WebApplication app, RecordingPublicToolsDb db, IBackgroundJobClient jobs) : IAsyncDisposable
{
    public RecordingPublicToolsDb Db { get; } = db;
    public IBackgroundJobClient Jobs { get; } = jobs;
    public IReadOnlyCollection<string> BracketRoutes => ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(source => source.Endpoints)
        .OfType<RouteEndpoint>()
        .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/tools/brackets", StringComparison.Ordinal) == true)
        .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
            .Select(method => $"{method} {endpoint.RoutePattern.RawText}") ?? [])
        .ToArray();

    public static async Task<PublicToolsTestHost> CreateAsync(IDbConnectionFactory? database = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.WebHost.UseTestServer();
        var db = new RecordingPublicToolsDb();
        var jobs = Substitute.For<IBackgroundJobClient>();
        builder.Services.AddSingleton<IDbConnectionFactory>(database ?? db);
        builder.Services.AddSingleton(jobs);
        builder.Services.AddScoped<PublicVetoOverlayUsageScheduler>();
        builder.Services.AddSignalR();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, PublicToolsTestAuth>("test", _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy("Authenticated", p => p.RequireAuthenticatedUser()));
        var app = builder.Build();
        app.UseAuthentication();
        app.Use(async (ctx, next) =>
        {
            if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.Identity.Name != "missing-context")
                ctx.Items["UserContext"] = new UserContext { AdminRoles = ctx.User.Identity.Name == "super" ? ["super_admin"] : [ctx.User.Identity.Name ?? "member"] };
            await next(ctx);
        });
        app.UseAuthorization();
        app.MapAdminPublicToolsEndpoints();
        app.MapPublicToolEndpoints();
        await app.StartAsync();
        return new PublicToolsTestHost(app, db, jobs);
    }

    public async Task<HttpResponseMessage> GetAsync(string path, string? identity = "super")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (identity is not null) request.Headers.Add("X-Test-Identity", identity);
        return await app.GetTestClient().SendAsync(request);
    }

    public async Task<HttpResponseMessage> PostJsonAsync<T>(string path, T body, string? identity = "super")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (identity is not null) request.Headers.Add("X-Test-Identity", identity);
        return await app.GetTestClient().SendAsync(request);
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();
}

internal sealed class PublicToolsTestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = Request.Headers["X-Test-Identity"].ToString();
        if (identity.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, identity), new Claim(ClaimTypes.Role, "super_admin")], "test"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, Scheme.Name)));
    }
}

internal sealed record RecordedPublicToolsCommand(string CommandText, Dictionary<string, object?> Parameters);

internal sealed class RecordingPublicToolsDb : IDbConnectionFactory
{
    public Queue<DataTable> Results { get; } = new();
    public List<RecordedPublicToolsCommand> Commands { get; } = [];
    public IDbConnection CreateConnection() => new PublicToolsConnection(this);
}

#pragma warning disable CS8765 // ADO.NET string setter nullability differs from the abstract annotation.
internal sealed class PublicToolsConnection(RecordingPublicToolsDb db) : DbConnection
{
    public override string ConnectionString { get; set; } = "fake";
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "0";
    public override ConnectionState State => ConnectionState.Open;
    public override void Open() { }
    public override void Close() { }
    public override void ChangeDatabase(string databaseName) { }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new PublicToolsTransaction(this, isolationLevel);
    protected override DbCommand CreateDbCommand() => new PublicToolsCommand(db);
}

internal sealed class PublicToolsTransaction(DbConnection connection, IsolationLevel isolationLevel) : DbTransaction
{
    public override IsolationLevel IsolationLevel => isolationLevel;
    protected override DbConnection DbConnection => connection;
    public override void Commit() { }
    public override void Rollback() { }
}

internal sealed class PublicToolsCommand(RecordingPublicToolsDb db) : DbCommand
{
    private readonly FakeDbParameterCollection _parameters = new();
    public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbConnection? DbConnection { get; set; }
    protected override DbTransaction? DbTransaction { get; set; }
    public override void Cancel() { }
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new FakeDbParameter();
    private void Capture() => db.Commands.Add(new(CommandText, _parameters.Cast<DbParameter>().ToDictionary(p => p.ParameterName, p => p.Value is DBNull ? null : p.Value)));
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        Capture();
        return (db.Results.Count > 0 ? db.Results.Dequeue() : new DataTable()).CreateDataReader();
    }
    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ExecuteDbDataReader(behavior));
    }
    public override int ExecuteNonQuery() { Capture(); return 1; }
    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ExecuteNonQuery());
    }
    public override object? ExecuteScalar() => throw new NotSupportedException();
}
#pragma warning restore CS8765
