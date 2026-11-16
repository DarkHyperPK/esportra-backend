using Esportra.Contracts.Auth;
using Hangfire.Dashboard;

namespace Esportra.Api.ScheduledJobs;

public sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        var env = httpContext.RequestServices.GetService<IHostEnvironment>();
        if (env?.IsDevelopment() == true)
            return true;

        var config = httpContext.RequestServices.GetService<IConfiguration>();
        var dashboardKey = config?["HangfireDashboardKey"];
        if (!string.IsNullOrEmpty(dashboardKey) &&
            httpContext.Request.Query.TryGetValue("key", out var providedKey) &&
            string.Equals(dashboardKey, providedKey, StringComparison.Ordinal))
        {
            return true;
        }

        if (httpContext.Items.TryGetValue("UserContext", out var obj) &&
            obj is UserContext userCtx &&
            userCtx.AdminRoles.Length > 0)
        {
            return true;
        }

        return false;
    }
}
