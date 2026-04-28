using System.Reflection;
using DbUp;
using DbUp.Engine;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Esportra.Infrastructure.Migrations;

/// <summary>Result of a schema compatibility check.</summary>
public sealed record SchemaCompatibilityResult(
    bool IsCompatible,
    IReadOnlyList<string> PendingScripts,
    string? FailureReason = null)
{
    public static SchemaCompatibilityResult Compatible { get; } =
        new(true, Array.Empty<string>());
}

public sealed class MigrationRunner
{
    private readonly string _connectionString;
    private readonly ILogger<MigrationRunner> _logger;

    // Migrations are embedded in THIS assembly (Esportra.Infrastructure), not the caller.
    // Using typeof() guarantees the correct assembly regardless of which executable calls Run().
    private static readonly Assembly MigrationsAssembly = typeof(MigrationRunner).Assembly;

    private const int MaxRetries = 15;
    private const int CompatibilityCheckMaxRetries = 5;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    private const double BackoffMultiplier = 1.5;

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

        if (!WaitForDatabase(
                operation: "migration execution",
                maxRetries: MaxRetries,
                failureMessage: "Database unreachable after {Max} attempts. Migration execution cannot continue."))
        {
            return false;
        }

        EnsureDatabase.For.PostgresqlDatabase(_connectionString);

        var upgrader = BuildUpgrader();

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

    /// <summary>
    /// Checks whether the database schema is compatible with the current codebase.
    /// Compatible means all embedded migration scripts have already been applied.
    /// Does NOT apply any migrations — use Run() for that.
    /// </summary>
    public SchemaCompatibilityResult CheckCompatibility()
    {
        if (!WaitForDatabase(
                operation: "schema compatibility check",
                maxRetries: CompatibilityCheckMaxRetries,
                failureMessage: "Database unreachable after {Max} attempts. Schema compatibility cannot be verified."))
        {
            return new SchemaCompatibilityResult(
                false,
                Array.Empty<string>(),
                "Could not connect to the database to verify schema compatibility.");
        }

        var upgrader = BuildUpgrader();

        if (!upgrader.IsUpgradeRequired())
            return SchemaCompatibilityResult.Compatible;

        var pending = upgrader.GetScriptsToExecute()
            .Select(s => s.Name)
            .ToList();

        _logger.LogError(
            "Schema incompatibility: {Count} migration(s) have not been applied: {Scripts}. " +
            "Run Esportra.Migrator before starting the API.",
            pending.Count,
            string.Join(", ", pending));

        return new SchemaCompatibilityResult(false, pending);
    }

    /// <summary>
    /// Waits for the Postgres server to accept connections, retrying with exponential backoff.
    /// Prevents crash when the backend starts before the database container is ready.
    /// </summary>
    private bool WaitForDatabase(string operation, int maxRetries, string failureMessage)
    {
        var delay = InitialDelay;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var conn = new NpgsqlConnection(_connectionString);
                conn.Open();
                _logger.LogInformation(
                    "Database is reachable for {Operation} (attempt {Attempt}).",
                    operation,
                    attempt);
                return true;
            }
            catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
            {
                _logger.LogWarning(
                    "Database not ready for {Operation} (attempt {Attempt}/{Max}): {Message}. Retrying in {Delay}s...",
                    operation,
                    attempt,
                    maxRetries,
                    ex.Message,
                    delay.TotalSeconds);
                if (attempt == maxRetries)
                {
                    break;
                }

                Thread.Sleep(delay);
                delay = TimeSpan.FromSeconds(delay.TotalSeconds * BackoffMultiplier);
            }
        }

        _logger.LogError(failureMessage, maxRetries);
        return false;
    }

    private DbUp.Engine.UpgradeEngine BuildUpgrader() =>
        DeployChanges.To
            .PostgresqlDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(
                MigrationsAssembly,
                s => s.Contains(".Migrations.Scripts."))
            .WithTransactionPerScript()
            .WithVariablesDisabled()
            .LogTo(new DbUpLogger(_logger))
            .Build();

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
