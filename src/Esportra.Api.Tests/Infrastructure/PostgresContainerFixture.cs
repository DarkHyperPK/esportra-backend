using DbUp;
using Esportra.Infrastructure.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace Esportra.Api.Tests.Infrastructure;

public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("esportra_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyBootstrapSchemaAsync();
        RunMigrations();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private void RunMigrations()
    {
        var upgrader = DbUp.DeployChanges.To
            .PostgresqlDatabase(ConnectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(MigrationRunner).Assembly,
                s => s.Contains(".Migrations.Scripts."))
            .WithTransactionPerScript()
            .WithVariablesDisabled()
            .LogToConsole()
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
            throw new InvalidOperationException(
                $"Test migration failed on: {result.ErrorScript?.Name ?? "unknown"}. Error: {result.Error?.Message}",
                result.Error);
    }

    private async Task ApplyBootstrapSchemaAsync()
    {
        var bootstrapPath = FindProjectFile(".github/ci/post-baseline-replay-schema.sql");
        var journalPath = FindProjectFile(".github/ci/replay-journal-seed.sql");

        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        if (File.Exists(bootstrapPath))
            await ExecuteSqlFileAsync(connection, bootstrapPath);
        if (File.Exists(journalPath))
            await ExecuteSqlFileAsync(connection, journalPath);
    }

    private static async Task ExecuteSqlFileAsync(Npgsql.NpgsqlConnection connection, string path)
    {
        var sql = await File.ReadAllTextAsync(path);
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private static string FindProjectFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            var sln = Directory.GetFiles(dir.FullName, "*.sln").FirstOrDefault();
            if (sln is not null) return Path.Combine(dir.FullName, relativePath);
            dir = dir.Parent;
        }
        return relativePath;
    }
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresContainerFixture>;
