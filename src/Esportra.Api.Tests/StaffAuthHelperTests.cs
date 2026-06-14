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

    [Fact]
    public void IsTournamentOrganizerOrOrgOwnerSql_CoversOrgOwnerWhenOrganizationIdUnset()
    {
        var helperSource = File.ReadAllText(FindStaffAuthHelperCs());

        Assert.Contains("o2.owner_id = @userId", helperSource, StringComparison.Ordinal);
        Assert.Contains("t.organization_id IS NULL", helperSource, StringComparison.Ordinal);
        Assert.Contains("organization_staff os", helperSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveEffectivePermissions_InheritsOrgWhenAssignmentNull()
    {
        var org = new[] { StaffAuthHelper.PermScoresUpdate, StaffAuthHelper.PermDisputesAssist };
        var effective = StaffAuthHelper.ResolveEffectivePermissions(org, null);
        Assert.Equal(org, effective);
    }

    [Fact]
    public void ResolveEffectivePermissions_UsesAssignmentOverrideWhenSet()
    {
        var org = new[] { StaffAuthHelper.PermScoresUpdate };
        var assignment = new[] { StaffAuthHelper.PermBracketEdit, StaffAuthHelper.PermDisputesAssist };
        var effective = StaffAuthHelper.ResolveEffectivePermissions(org, assignment);
        Assert.Equal(assignment, effective);
    }

    [Fact]
    public void ResolveEffectivePermissions_DeduplicatesAndIgnoresBlank()
    {
        var effective = StaffAuthHelper.ResolveEffectivePermissions(
            new[] { StaffAuthHelper.PermScoresUpdate, StaffAuthHelper.PermScoresUpdate, "  " },
            null);
        Assert.Single(effective);
        Assert.Equal(StaffAuthHelper.PermScoresUpdate, effective[0]);
    }

    [Fact]
    public void TryNormalizeStaffPermissions_RejectsUnknown()
    {
        var ok = StaffAuthHelper.TryNormalizeStaffPermissions(
            new[] { StaffAuthHelper.PermBracketEdit, "invalid:perm" },
            out _,
            out var error);
        Assert.False(ok);
        Assert.Contains("invalid:perm", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryNormalizeStaffPermissions_AcceptsKnownCatalog()
    {
        var ok = StaffAuthHelper.TryNormalizeStaffPermissions(
            new[] { StaffAuthHelper.PermTeamsManage, StaffAuthHelper.PermTeamsManage },
            out var normalized,
            out _);
        Assert.True(ok);
        Assert.Single(normalized);
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
