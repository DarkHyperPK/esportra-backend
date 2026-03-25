using System.Reflection;
using DbUp;
using Microsoft.Extensions.Logging;

namespace Esportra.Infrastructure.Migrations;

public sealed class MigrationRunner
{
    private readonly string _connectionString;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(string connectionString, ILogger<MigrationRunner> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <summary>
    /// Runs all pending SQL migrations from the embedded Scripts folder.
    /// Returns true if successful, false if any migration failed.
    /// </summary>
    public bool Run()
    {
        _logger.LogInformation("Starting database migration check...");

        EnsureDatabase.For.PostgresqlDatabase(_connectionString);

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                s => s.Contains(".Migrations.Scripts."))
            .WithTransactionPerScript()
            .WithVariablesDisabled()
            .LogTo(new DbUpLogger(_logger))
            .Build();

        if (!upgrader.IsUpgradeRequired())
        {
            _logger.LogInformation("Database is up to date. No migrations to run.");
            return true;
        }

        var pending = upgrader.GetScriptsToExecute();
        _logger.LogInformation("Found {Count} pending migration(s): {Scripts}",
            pending.Count,
            string.Join(", ", pending.Select(s => s.Name)));

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            _logger.LogError(result.Error, "Migration failed on script: {Script}",
                result.ErrorScript?.Name ?? "unknown");
            return false;
        }

        _logger.LogInformation("All migrations applied successfully.");
        return true;
    }

    /// <summary>Adapter to route DbUp log output through Microsoft.Extensions.Logging.</summary>
    private sealed class DbUpLogger : DbUp.Engine.Output.IUpgradeLog
    {
        private readonly ILogger _logger;
        public DbUpLogger(ILogger logger) => _logger = logger;
        public void WriteInformation(string format, params object[] args) => _logger.LogInformation(format, args);
        public void WriteWarning(string format, params object[] args) => _logger.LogWarning(format, args);
        public void WriteError(string format, params object[] args) => _logger.LogError(format, args);
    }
}
