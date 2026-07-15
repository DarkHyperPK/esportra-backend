using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;

namespace Esportra.Api.Auth;

/// <summary>
/// Authorization requirement that checks a specific permission string (resource:action).
/// Usage: [Authorize(Policy = "users:ban")]
/// </summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Evaluates whether the current user's enriched context contains the required permission.
/// The UserContext is populated by RoleEnrichmentMiddleware and stored in HttpContext.Items.
/// </summary>
public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.Resource is HttpContext httpContext &&
            httpContext.Items.TryGetValue("UserContext", out var obj) &&
            obj is UserContext userCtx)
        {
            if (userCtx.IsSuperAdmin ||
                userCtx.Permissions.Contains(requirement.Permission, StringComparer.OrdinalIgnoreCase))
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Requires the user to have at least one admin role (populated from
/// admin_user_roles / profiles.admin_roles by RoleEnrichmentMiddleware).
/// </summary>
public sealed class AdminRequirement : IAuthorizationRequirement;

public sealed class AdminHandler : AuthorizationHandler<AdminRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminRequirement requirement)
    {
        if (context.Resource is HttpContext httpContext &&
            httpContext.Items.TryGetValue("UserContext", out var obj) &&
            obj is UserContext userCtx &&
            userCtx.AdminRoles.Length > 0)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
