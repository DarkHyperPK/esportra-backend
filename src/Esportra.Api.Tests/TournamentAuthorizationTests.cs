using Esportra.Api.Helpers;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Xunit;

namespace Esportra.Api.Tests;

public class TournamentAuthorizationTests
{
    [Fact]
    public void IsPlatformAdmin_Succeeds_ForSuperAdmin()
    {
        var user = new UserContext
        {
            UserId = Guid.NewGuid().ToString(),
            AdminRoles = [AdminRoles.SuperAdmin],
        };

        Assert.True(StaffAuthHelper.IsPlatformAdmin(user));
        Assert.True(TournamentAuthorizationService.IsPlatformAdmin(user));
    }

    [Fact]
    public void IsPlatformAdmin_Succeeds_ForAnyAdminRole()
    {
        var user = new UserContext
        {
            UserId = Guid.NewGuid().ToString(),
            AdminRoles = [AdminRoles.SupportAdmin],
        };

        Assert.True(StaffAuthHelper.IsPlatformAdmin(user));
    }

    [Fact]
    public void IsPlatformAdmin_Fails_ForSessionOrganizerRoleOnly()
    {
        var user = new UserContext
        {
            UserId = Guid.NewGuid().ToString(),
            Roles = ["organizer", "admin"],
            AdminRoles = [],
        };

        Assert.False(StaffAuthHelper.IsPlatformAdmin(user));
    }

    [Fact]
    public void IsPlatformAdmin_Succeeds_ForAnyAdminRole_ButMatchRoomRequiresPermission()
    {
        var user = new UserContext
        {
            UserId = Guid.NewGuid().ToString(),
            AdminRoles = [AdminRoles.SupportAdmin],
            Permissions = [Permissions.UsersView],
        };

        Assert.True(StaffAuthHelper.IsPlatformAdmin(user));
        Assert.False(StaffAuthHelper.HasMatchRoomAdminPermission(user));
    }

    [Fact]
    public void HasMatchRoomAdminPermission_Succeeds_ForDisputesView()
    {
        var user = new UserContext
        {
            UserId = Guid.NewGuid().ToString(),
            AdminRoles = [AdminRoles.Moderator],
            Permissions = [Permissions.DisputesView],
        };

        Assert.True(StaffAuthHelper.HasMatchRoomAdminPermission(user));
    }

    [Fact]
    public void BrHardeningMigration_RevokesAuthenticatedWrites()
    {
        var migrationSql = File.ReadAllText(FindBrHardeningMigration());

        Assert.Contains("REVOKE ALL PRIVILEGES", migrationSql, StringComparison.Ordinal);
        Assert.Contains("br_groups", migrationSql, StringComparison.Ordinal);
        Assert.Contains("br_round_evidence", migrationSql, StringComparison.Ordinal);
        Assert.Contains("_authenticated_", migrationSql, StringComparison.Ordinal);
        Assert.Contains("DROP POLICY IF EXISTS", migrationSql, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON public.", migrationSql, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramCs_NoLongerDefinesMisleadingOrganizerPolicy()
    {
        var programCs = File.ReadAllText(FindProgramCs());

        Assert.DoesNotContain("AddPolicy(\"Organizer\"", programCs, StringComparison.Ordinal);
        Assert.DoesNotContain("AddPolicy(\"VenueOwner\"", programCs, StringComparison.Ordinal);
        Assert.Contains("AddPolicy(\"Authenticated\"", programCs, StringComparison.Ordinal);
    }

    private static string FindBrHardeningMigration()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "src",
                "Esportra.Infrastructure",
                "Migrations",
                "Scripts",
                "20260610193000_harden_br_table_grants.sql");

            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate BR hardening migration.");
    }

    private static string FindProgramCs()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "Esportra.Api", "Program.cs");
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate Program.cs.");
    }
}
