using System.Text.RegularExpressions;
using Esportra.Contracts.Competitive;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

public sealed class ScrimCancelReasonContractTests
{
    private static string FindMigration()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var scripts = Path.Combine(dir.FullName, "src", "Esportra.Infrastructure", "Migrations", "Scripts");
            if (Directory.Exists(scripts))
                return Directory.GetFiles(scripts, "*_scrim_session_cancel_reason.sql").Single();
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Migration scripts directory not found.");
    }

    /// <summary>The newest migration that defines the constraint is the one in force.</summary>
    private static string FindLatestConstraintMigration() =>
        Directory.GetFiles(Path.GetDirectoryName(FindMigration())!, "*.sql")
            .Where(f => File.ReadAllText(f).Contains("ADD CONSTRAINT chk_scrim_sessions_cancelled_reason"))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).Last();

    [Fact]
    public void Migration_CheckConstraint_MatchesScrimCancelReasons()
    {
        var sql = File.ReadAllText(FindLatestConstraintMigration());

        var check = Regex.Match(sql, @"cancelled_reason IN \(([^)]*)\)");
        check.Success.Should().BeTrue("the migration must constrain cancelled_reason");

        var inMigration = Regex.Matches(check.Groups[1].Value, "'([a-z_]+)'")
            .Select(m => m.Groups[1].Value);

        inMigration.Should().BeEquivalentTo(ScrimCancelReasons.All);
    }

    [Fact]
    public void Migration_IsIdempotent_AndDoesNotBackfill()
    {
        var sql = File.ReadAllText(FindMigration());

        sql.Should().Contain("ADD COLUMN IF NOT EXISTS cancelled_reason");
        sql.Should().Contain("IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_sessions_cancelled_reason')");
        Regex.IsMatch(sql, @"^\s*(UPDATE|DELETE)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Should().BeFalse("legacy rows stay NULL; no bulk data changes");
    }

    [Fact]
    public void TimeoutJob_And_CancelEndpoint_UseKnownReasons()
    {
        var root = Path.GetDirectoryName(FindMigration())!;
        var api = Path.GetFullPath(Path.Combine(root, "..", "..", "..", "Esportra.Api"));

        File.ReadAllText(Path.Combine(api, "ScheduledJobs", "ScrimCheckinTimeoutJob.cs"))
            .Should().Contain($"'{ScrimCancelReasons.CheckinTimeout}'");
        File.ReadAllText(Path.Combine(api, "Endpoints", "ScrimEndpoints.cs"))
            .Should().Contain($"'{ScrimCancelReasons.CancelledByTeam}'");
    }
}
