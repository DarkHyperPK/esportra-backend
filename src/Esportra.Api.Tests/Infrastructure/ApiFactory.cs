using Esportra.Contracts.Database;
using Esportra.Infrastructure.Database;
using Esportra.Infrastructure.Migrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Esportra.Api.Tests.Infrastructure;

/// <summary>
/// WebApplicationFactory that boots the full API against a Testcontainers Postgres instance.
/// One factory is shared per <see cref="TestDatabaseCollection"/> via class fixtures.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly TestDatabase _db;

    public ApiFactory(TestDatabase db) => _db = db;

    /// <summary>A <see cref="DbSeeder"/> pre-wired to the test database.</summary>
    public DbSeeder Seeder => new(_db.ConnectionString);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("ConnectionStrings:Postgres", _db.ConnectionString);
        builder.UseSetting("ConnectionStrings:PostgresMigrations", _db.ConnectionString);
        builder.UseSetting("ConnectionStrings:Hangfire", _db.ConnectionString);

        // Use a known test JWT secret so tests can generate valid tokens
        builder.UseSetting("Supabase:JwtSecret", TestAuthHelper.TestJwtSecret);
        builder.UseSetting("Supabase:JwtAudience", TestAuthHelper.TestAudience);
        builder.UseSetting("Supabase:JwtIssuer", "");

        // Disable file storage (no Supabase bucket in tests)
        builder.UseSetting("Supabase:ServiceKey", "test-service-key");
        builder.UseSetting("Supabase:Url", "http://localhost:54321");

        // Disable Redis — SwappableDistributedCache falls back to in-memory automatically
        builder.UseSetting("Redis:ConnectionString", "");

        // Email — point at a no-op sink
        builder.UseSetting("Resend:ApiKey", "re_test_00000000000000000000000000000000");

        // Migrations already applied by TestDatabase.InitializeAsync(); skip re-run check.
        // Setting IsDevelopment() → runMigrationsOnStartup = true, but DbUp detects
        // no pending scripts and exits in ~5 ms.
        builder.UseSetting("Database:RunMigrationsOnStartup", "true");

        builder.ConfigureServices(services =>
        {
            // Replace the singleton IDbConnectionFactory with one pointing at test DB
            services.RemoveAll<IDbConnectionFactory>();
            services.AddSingleton<IDbConnectionFactory>(
                new NpgsqlConnectionFactory(_db.ConnectionString));
        });
    }

    /// <summary>Creates an HttpClient with the Authorization header set for <paramref name="userId"/>.</summary>
    public HttpClient CreateAuthenticatedClient(Guid userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", TestAuthHelper.BearerHeader(userId));
        return client;
    }
}
