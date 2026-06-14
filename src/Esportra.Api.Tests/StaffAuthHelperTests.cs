using Esportra.Api.Helpers;
using Xunit;

namespace Esportra.Api.Tests;

/// <summary>
/// Guards staff authorization invariants: write paths delegate through ResolveStaffAccessAsync.
/// </summary>
public class StaffAuthHelperTests
{
    [Fact]
    public void StaffOrgTournamentLinkSql_IsUsedByStaffTournamentAccessExistsSql()
    {
        Assert.Contains(StaffAuthHelper.StaffOrgTournamentLinkSql.Trim(), StaffAuthHelper.StaffTournamentAccessExistsSql);
    }

    [Fact]
    public void AllStaffPermissions_ContainsDisputeAndBracketPerms()
    {
        Assert.Contains(StaffAuthHelper.PermDisputesAssist, StaffAuthHelper.AllStaffPermissions);
        Assert.Contains(StaffAuthHelper.PermBracketEdit, StaffAuthHelper.AllStaffPermissions);
    }

    [Fact]
    public void CanActOnTournamentAsync_DelegatesToResolveStaffAccess_NotLegacyOrgIdJoin()
    {
        var helperSource = File.ReadAllText(FindStaffAuthHelperCs());

        Assert.Contains("ResolveStaffAccessAsync", helperSource, StringComparison.Ordinal);
        Assert.Contains("IsTournamentOrganizerOrOrgOwnerAsync", helperSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AND (os.organization_id = t.organization_id)", helperSource, StringComparison.Ordinal);
    }

    private static string FindStaffAuthHelperCs()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "Esportra.Api", "Helpers", "StaffAuthHelper.cs");
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate StaffAuthHelper.cs.");
    }
}
