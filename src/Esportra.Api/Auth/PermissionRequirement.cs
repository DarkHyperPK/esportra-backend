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
            if (userCtx.Permissions.Contains(requirement.Permission))
                context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
