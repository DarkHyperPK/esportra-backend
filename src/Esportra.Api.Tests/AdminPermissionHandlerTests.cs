using System.Security.Claims;
using Esportra.Api.Auth;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Esportra.Api.Tests;

public class AdminPermissionHandlerTests
{
    [Fact]
    public async Task PermissionHandler_Succeeds_WhenUserHasRequiredPermission()
    {
        var context = CreateAuthorizationContext(new UserContext
        {
            UserId = Guid.NewGuid().ToString(),
            Permissions = [Permissions.UsersView],
        }, Permissions.UsersView);

        await new PermissionHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task PermissionHandler_Succeeds_ForSuperAdminWithoutExplicitPermission()
    {
        var context = CreateAuthorizationContext(new UserContext
        {
            UserId = Guid.NewGuid().ToString(),
            AdminRoles = [AdminRoles.SuperAdmin],
            Permissions = [],
        }, Permissions.DataAdminOverride);

        await new PermissionHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task PermissionHandler_Fails_WhenPermissionIsMissing()
    {
        var context = CreateAuthorizationContext(new UserContext
        {
            UserId = Guid.NewGuid().ToString(),
            AdminRoles = [AdminRoles.SupportAdmin],
            Permissions = [Permissions.UsersView],
        }, Permissions.RbacDeleteRole);

        await new PermissionHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public void SuperAdminRolePermissions_IncludeEveryPermissionConstant()
    {
        var expectedPermissions = typeof(Permissions)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        var actualPermissions = AdminRoles.RolePermissions[AdminRoles.SuperAdmin]
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(expectedPermissions.SetEquals(actualPermissions));
    }

    [Fact]
    public void BuiltInRolePermissions_OnlyReferenceKnownPermissionConstants()
    {
        var knownPermissions = GetPermissionConstants();

        var unknownPermissions = AdminRoles.RolePermissions
            .SelectMany(role => role.Value.Select(permission => new { role.Key, Permission = permission }))
            .Where(entry => !knownPermissions.Contains(entry.Permission))
            .ToArray();

        Assert.Empty(unknownPermissions);
    }

    [Fact]
    public void RbacV2Migration_SeedsEveryPermissionConstant()
    {
        var migrationSql = File.ReadAllText(FindRbacV2Migration());
        var missingPermissions = GetPermissionConstants()
            .Where(permission => !migrationSql.Contains($"'{permission}'", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(missingPermissions);
    }

    private static AuthorizationHandlerContext CreateAuthorizationContext(
        UserContext userContext,
        string requiredPermission)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["UserContext"] = userContext;

        var requirement = new PermissionRequirement(requiredPermission);
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        return new AuthorizationHandlerContext([requirement], principal, httpContext);
    }

    private static HashSet<string> GetPermissionConstants() =>
        typeof(Permissions)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

    private static string FindRbacV2Migration()
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
                "20260608181500_admin_permission_catalog_v2.sql");

            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate RBAC v2 migration.");
    }
}
