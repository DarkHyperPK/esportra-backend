using DotNet.Testcontainers.Builders;
using Esportra.Infrastructure.Migrations;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Esportra.Api.Tests.Infrastructure;

/// <summary>
/// Shared Postgres container + migrated schema for the whole test session.
/// Used as an xUnit collection fixture — one container per test run.
/// </summary>
public sealed class TestDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:15-alpine")
        .WithDatabase("esportra_test")
        .WithUsername("postgres")
        .WithPassword("test")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyBootstrapAsync();
        RunMigrations();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    // ── Helpers ──────────────────────────────────────────────────────────────

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
    }

    private void RunMigrations()
    {
        var runner = new MigrationRunner(ConnectionString, NullLogger<MigrationRunner>.Instance);
        if (!runner.Run())
            throw new InvalidOperationException("Test database migration failed.");
    }

    private static string ResolveRepoPath(string relativePath)
    {
        // Walk up from the test assembly directory to find the repo root (contains .git)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException("Could not locate repository root (.git directory).");

        return Path.Combine(dir.FullName, relativePath);
    }
}

/// <summary>xUnit collection that shares one <see cref="TestDatabase"/> across all member classes.</summary>
[CollectionDefinition(Name)]
public sealed class TestDatabaseCollection : ICollectionFixture<TestDatabase>
{
    public const string Name = "TestDatabase";
}
