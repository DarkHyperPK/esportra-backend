using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

/// <summary>
/// Guards the rules of agreeing a scrim time that only show up in SQL and endpoint source:
/// who may answer a proposal, when the time is frozen, and that the table is locked down.
/// Behaviour was verified against PostgreSQL 16; these keep the contract honest in CI.
/// </summary>
public sealed class ScrimScheduleContractTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Esportra.Api"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Migration(string suffix) => File.ReadAllText(Directory
        .GetFiles(Path.Combine(Root(), "src", "Esportra.Infrastructure", "Migrations", "Scripts"), $"*_{suffix}.sql").Single());

    private static string Endpoints() =>
        File.ReadAllText(Path.Combine(Root(), "src", "Esportra.Api", "Endpoints", "ScrimScheduleEndpoints.cs"));

    [Fact]
    public void Table_HasRowSecurity_AndOnlyStaffMayReadIt()
    {
        var sql = Migration("scrim_time_proposals");
        sql.Should().Contain("ALTER TABLE public.scrim_time_proposals ENABLE ROW LEVEL SECURITY");
        sql.Should().Contain("FOR SELECT TO authenticated");
        sql.Should().Contain("tm.role IN ('captain', 'manager')");
        Regex.IsMatch(sql, @"FOR (INSERT|UPDATE|DELETE|ALL) TO authenticated").Should().BeFalse("writes go through the API only");
        sql.Should().Contain("FOR ALL TO service_role");
    }

    [Fact]
    public void Table_AllowsOnePendingProposalPerSession_AndOnlyKnownStatuses()
    {
        var sql = Migration("scrim_time_proposals");
        sql.Should().Contain("CREATE UNIQUE INDEX IF NOT EXISTS uq_scrim_time_proposals_one_pending");
        sql.Should().Contain("WHERE status = 'pending'");
        sql.Should().Contain("('pending', 'accepted', 'declined', 'superseded')");
    }

    [Fact]
    public void Migrations_AreRerunnable_AndMakeNoBulkDataChanges()
    {
        var table = Migration("scrim_time_proposals");
        table.Should().Contain("CREATE TABLE IF NOT EXISTS");
        table.Should().Contain("IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_scrim_time_proposals_status')");
        Regex.IsMatch(table, @"^\s*(UPDATE|DELETE)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase).Should().BeFalse();

        var enums = Migration("scrim_time_notification_types");
        foreach (var type in new[] { "scrim_time_proposed", "scrim_time_accepted", "scrim_time_declined" })
            enums.Should().Contain($"ADD VALUE IF NOT EXISTS '{type}'");
    }

    [Fact]
    public void OnlyTheOtherTeamMayAcceptOrDecline()
    {
        var src = Endpoints();
        src.Should().Contain("var responder = p.ProposedByTeamId == p.Team1Id ? p.Team2Id : p.Team1Id;");
        src.Should().Contain("StaffTeamAsync(conn, user.UserIdGuid, responder) is null");
    }

    [Fact]
    public void TheTimeCannotChange_OnceCheckInHasStartedOrTheScrimIsOver()
    {
        var src = Endpoints();
        src.Should().Contain("status != \"scheduled\"");
        src.Should().Contain("t1CheckedIn is not null || t2CheckedIn is not null");
        // Both proposing and accepting go through the same guard.
        Regex.Matches(src, @"Frozen\(").Count.Should().BeGreaterOrEqualTo(3);
    }

    [Fact]
    public void AcceptingAProposal_IsAtomic_RestartsCheckIn_AndReschedulesTheJobs()
    {
        var src = Endpoints();
        src.Should().Contain("WHERE id = @id AND status = 'pending' RETURNING id");
        src.Should().Contain("checkin_window_opened_at = NULL");
        src.Should().Contain("ScrimJobScheduler.ScheduleAsync");
    }

    [Fact]
    public void ProposingSupersedesTheOldProposal_InTheSameTransaction()
    {
        var src = Endpoints();
        var supersede = src.IndexOf("SET status = 'superseded'", StringComparison.Ordinal);
        var insert = src.IndexOf("INSERT INTO public.scrim_time_proposals", StringComparison.Ordinal);
        supersede.Should().BeGreaterThan(0);
        insert.Should().BeGreaterThan(supersede);
        src.Should().Contain("using var tx = conn.BeginTransaction();");
    }

    [Fact]
    public void AcceptingAnOffer_NoLongerValidatesAvailability_OrRequiresATime()
    {
        var src = File.ReadAllText(Path.Combine(Root(), "src", "Esportra.Api", "Endpoints", "ScrimEndpoints.cs"));
        src.Should().NotContain("must be between");
        src.Should().NotContain("outside the listing's available days");
        src.Should().Contain("ScrimAcceptPlan.For(");
    }
}
