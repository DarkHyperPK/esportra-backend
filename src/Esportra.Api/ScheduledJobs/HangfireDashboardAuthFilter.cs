using Esportra.Contracts.Auth;
using Hangfire.Dashboard;

namespace Esportra.Api.ScheduledJobs;

public sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        // Allow in development without auth
        var env = httpContext.RequestServices.GetService<IHostEnvironment>();
        if (env?.IsDevelopment() == true)
            return true;

        if (httpContext.Items.TryGetValue("UserContext", out var obj) &&
            obj is UserContext userCtx &&
            userCtx.AdminRoles.Length > 0)
        {
            return true;
        }

        return false;
    }
}
