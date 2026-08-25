using Xunit;

namespace Esportra.Api.Tests;

public class AdminUserDetailTests
{
    [Fact]
    public void UserDetailTeamsQuery_UsesIsActiveNotStatus()
    {
        var source = File.ReadAllText(FindAdminEndpointsFile());
        Assert.Contains("tm.is_active = TRUE", source, StringComparison.Ordinal);
        Assert.DoesNotContain("tm.status = 'accepted'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UserDetailEndpoint_RequiresUsersViewPermission()
    {
        var source = File.ReadAllText(FindAdminEndpointsFile());
        Assert.Contains("RequireAuthorization(Permissions.UsersView)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionEndpoints_UseSecurityPermissions()
    {
        var source = File.ReadAllText(FindAdminEndpointsFile());
        Assert.Contains("RequireAuthorization(Permissions.SecurityViewSessions)", source, StringComparison.Ordinal);
        Assert.Contains("RequireAuthorization(Permissions.SecurityRevokeSessions)", source, StringComparison.Ordinal);
        Assert.Contains("HasSecurityPermission(userCtx, Permissions.SecurityViewSessions)", source, StringComparison.Ordinal);
        Assert.Contains("HasSecurityPermission(userCtx, Permissions.SecurityRevokeSessions)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityHistoryEndpoint_CastsAuditTargetIdsToText()
    {
        var source = File.ReadAllText(FindAdminEndpointsFile());
        Assert.Contains("target_id::text = @targetIdText", source, StringComparison.Ordinal);
        Assert.Contains("lower(target_type) = @targetType", source, StringComparison.Ordinal);
    }

    private static string FindAdminEndpointsFile()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "src",
                "Esportra.Api",
                "Endpoints",
                "AdminEndpoints.cs");

            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate AdminEndpoints.cs");
    }
}
