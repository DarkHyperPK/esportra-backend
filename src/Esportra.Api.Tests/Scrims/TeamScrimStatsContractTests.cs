using System.Text.RegularExpressions;
using Esportra.Contracts.Competitive;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

/// <summary>
/// Guards the properties of public.team_scrim_stats that must never silently change.
/// Behaviour (W-L-D, dispute override, no-show attribution) was verified against
/// PostgreSQL 16 fixtures; these tests keep the privacy and permission contract honest in CI.
/// </summary>
public sealed class TeamScrimStatsContractTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Esportra.Infrastructure")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static string Migration() => File.ReadAllText(Directory
        .GetFiles(Path.Combine(Root(), "src", "Esportra.Infrastructure", "Migrations", "Scripts"), "*_team_scrim_stats.sql")
        .Single());

    [Fact]
    public void Function_OnlyReturnsTeamsWithPublicHistory()
    {
        var sql = Migration();
        sql.Should().Contain("t.scrim_history_public = TRUE");
        // Eligibility is the only source of output rows, so private teams can never appear.
        sql.Should().Contain("FROM eligible e");
    }

    [Fact]
    public void Function_IsLockedToServiceRole()
    {
        var sql = Migration();
        foreach (var role in new[] { "PUBLIC", "anon", "authenticated" })
            sql.Should().Contain($"REVOKE EXECUTE ON FUNCTION public.team_scrim_stats(uuid[]) FROM {role};");
        sql.Should().Contain("GRANT EXECUTE ON FUNCTION public.team_scrim_stats(uuid[]) TO service_role;");
    }

    [Fact]
    public void Function_IsRerunnable_AndMakesNoBulkDataChanges()
    {
        var sql = Migration();
        sql.Should().Contain("CREATE OR REPLACE FUNCTION public.team_scrim_stats");
        sql.Should().Contain("CREATE INDEX IF NOT EXISTS idx_scrim_sessions_team1_status");
        sql.Should().Contain("CREATE INDEX IF NOT EXISTS idx_scrim_sessions_team2_status");
        Regex.IsMatch(sql, @"^\s*(UPDATE|DELETE|INSERT)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Should().BeFalse();
    }

    [Fact]
    public void Function_CountsOnlyCheckinTimeouts_AsNoShows()
    {
        Migration().Should().Contain($"g.cancelled_reason = '{ScrimCancelReasons.CheckinTimeout}'");
    }

    [Fact]
    public void ServiceQuery_ReadsEveryColumnTheFunctionReturns()
    {
        var service = File.ReadAllText(Path.Combine(Root(), "src", "Esportra.Api", "Services", "ScrimTeamStatsService.cs"));
        foreach (var column in new[] { "team_id", "wins", "losses", "draws", "completed", "no_shows", "show_rate", "last_played_at", "last5", "streak" })
        {
            service.Should().Contain(column);
            Migration().Should().Contain(column);
        }
    }

    [Fact]
    public void ScrimTeamRecord_DefaultsAreSafeForNewTeams()
    {
        var record = new ScrimTeamRecord();
        record.Last5.Should().BeEmpty();
        record.ShowRate.Should().BeNull();
        record.Streak.Should().Be(0);
    }
}
