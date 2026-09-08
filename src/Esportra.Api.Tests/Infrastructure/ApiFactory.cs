using DotNet.Testcontainers.Builders;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Database;
using Esportra.Infrastructure.Migrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Esportra.Api.Tests.Infrastructure;

/// <summary>
/// Boots a Testcontainers Postgres instance, applies the CI replay bootstrap,
/// runs DbUp migrations, then exposes a WebApplicationFactory pointing at that DB.
/// Shared as a collection fixture — one container per test session.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:15-alpine")
        .WithDatabase("esportra_test")
        .WithUsername("postgres")
        .WithPassword("test")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
        .Build();

    public string ConnectionString => _db.GetConnectionString();

    /// <summary>A <see cref="DbSeeder"/> pre-wired to the test database.</summary>
    public DbSeeder Seeder => new(ConnectionString);

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    async Task IAsyncLifetime.InitializeAsync()
    {
        await _db.StartAsync();
        await ApplyBootstrapAsync();
        RunMigrations();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _db.DisposeAsync();
        await base.DisposeAsync();
    }

    // ── WebApplicationFactory ─────────────────────────────────────────────────

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // AdminMutationAudit middleware injects a scoped service via constructor.
        // Development mode enables DI scope validation which blocks startup when
        // a scoped service is captured at the root provider level. Disable it here
        // so the test host builds correctly without changing production registration.
        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = false;
            options.ValidateOnBuild = false;
        });
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("ConnectionStrings:Postgres", ConnectionString);
        builder.UseSetting("ConnectionStrings:PostgresMigrations", ConnectionString);
        builder.UseSetting("ConnectionStrings:Hangfire", ConnectionString);

        builder.UseSetting("Supabase:JwtSecret", TestAuthHelper.TestJwtSecret);
        builder.UseSetting("Supabase:JwtAudience", TestAuthHelper.TestAudience);
        builder.UseSetting("Supabase:JwtIssuer", "");
        builder.UseSetting("Supabase:ServiceKey", "test-service-key");
        builder.UseSetting("Supabase:Url", "http://localhost:54321");

        // SwappableDistributedCache falls back to in-memory when Redis is unavailable
        builder.UseSetting("Redis:ConnectionString", "");

        // No-op email sink
        builder.UseSetting("Resend:ApiKey", "re_test_00000000000000000000000000000000");

        // DbUp already ran in InitializeAsync; setting Development makes startup
        // call Run() again but DbUp detects no pending scripts and exits in ~5 ms.
        builder.UseSetting("Database:RunMigrationsOnStartup", "true");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbConnectionFactory>();
            services.AddSingleton<IDbConnectionFactory>(new NpgsqlConnectionFactory(ConnectionString));
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates an HttpClient with Authorization header set for <paramref name="userId"/>.</summary>
    public HttpClient CreateAuthenticatedClient(Guid userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestAuthHelper.BearerHeader(userId));
        return client;
    }

    private async Task ApplyBootstrapAsync()
    {
        var schemaPath = ResolveRepoPath(".github/ci/post-baseline-replay-schema.sql");
        var journalPath = ResolveRepoPath(".github/ci/replay-journal-seed.sql");

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        foreach (var path in new[] { schemaPath, journalPath })
        {
            var sql = await File.ReadAllTextAsync(path);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        // The CI replay auth.users stub has only 4 columns (id, email, created_at, deleted_at).
        // Endpoint SQL references email_confirmed_at — add it so queries don't error.
        await using var patch = new NpgsqlCommand(
            "ALTER TABLE auth.users ADD COLUMN IF NOT EXISTS email_confirmed_at timestamptz;",
            conn);
        await patch.ExecuteNonQueryAsync();

        // auth.identities is managed by Supabase and absent from the replay schema.
        // Discord unlink endpoint and seeder both query/insert it.
        await using var identitiesPatch = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS auth.identities (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                user_id uuid NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
                provider text NOT NULL,
                provider_id text NOT NULL,
                identity_data jsonb NOT NULL DEFAULT '{}'::jsonb,
                created_at timestamptz DEFAULT NOW(),
                updated_at timestamptz DEFAULT NOW(),
                UNIQUE (provider, provider_id)
            );
            """,
            conn);
        await identitiesPatch.ExecuteNonQueryAsync();
    }

    private void RunMigrations()
    {
        var runner = new MigrationRunner(ConnectionString, NullLogger<MigrationRunner>.Instance);
        if (!runner.Run())
            throw new InvalidOperationException("Test database migration failed.");
    }

    private static string ResolveRepoPath(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException("Could not locate repository root (.git directory).");

        return Path.Combine(dir.FullName, relativePath);
    }
}

/// <summary>xUnit collection that shares one <see cref="ApiFactory"/> across all member test classes.</summary>
[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "Integration";
}
