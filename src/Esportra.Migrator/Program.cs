using Esportra.Infrastructure.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Esportra.Migrator");
var configuration = host.Services.GetRequiredService<IConfiguration>();
var environment = host.Services.GetRequiredService<IHostEnvironment>();
var checkCompatibilityOnly = args.Any(arg =>
    string.Equals(arg, "--check-compatibility", StringComparison.OrdinalIgnoreCase));

var migrationConnStr = configuration.GetConnectionString("PostgresMigrations")
    ?? configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:PostgresMigrations or ConnectionStrings:Postgres is required.");

logger.LogInformation("Starting dedicated database migrator in {Environment}.", environment.EnvironmentName);
logger.LogInformation(
    "Using {ConnectionName} for database migrations.",
    configuration.GetConnectionString("PostgresMigrations") is not null ? "PostgresMigrations" : "Postgres");

try
{
    var migrationRunnerLogger = host.Services.GetRequiredService<ILogger<MigrationRunner>>();
    var migrationRunner = new MigrationRunner(migrationConnStr, migrationRunnerLogger);

    if (checkCompatibilityOnly)
    {
        var compatibility = migrationRunner.CheckCompatibility();
        if (!compatibility.IsCompatible)
        {
            if (!string.IsNullOrWhiteSpace(compatibility.FailureReason))
            {
                logger.LogError("Schema compatibility check failed: {Reason}", compatibility.FailureReason);
                return 1;
            }

            logger.LogError(
                "Schema compatibility check failed. Pending migration(s): {Scripts}",
                string.Join(", ", compatibility.PendingScripts));
            return 1;
        }

        logger.LogInformation("Schema compatibility check passed.");
        return 0;
    }

    if (!migrationRunner.Run())
    {
        logger.LogError("Dedicated database migrator failed.");
        return 1;
    }

    logger.LogInformation("Dedicated database migrator completed successfully.");
    return 0;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Dedicated database migrator failed unexpectedly.");
    return 1;
}
