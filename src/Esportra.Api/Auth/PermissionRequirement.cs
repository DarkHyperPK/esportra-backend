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
/// Also enforces MFA when required — admin permissions derive from admin roles,
/// so the same 2FA enforcement applies.
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
            if (userCtx.Permissions.Contains(requirement.Permission))
            {
                // Permissions derive from admin roles — enforce MFA the same way
                if (userCtx.MfaRequired && userCtx.Aal != "aal2")
                {
                    httpContext.Items["MfaEnforcementBlocked"] = true;
                    return Task.CompletedTask;
                }

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
            // If MFA is required for this user's role but they only have aal1, deny access
            if (userCtx.MfaRequired && userCtx.Aal != "aal2")
            {
                httpContext.Items["MfaEnforcementBlocked"] = true;
                return Task.CompletedTask;
            }

            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
