using Esportra.Contracts.Auth;
using Hangfire.Dashboard;

namespace Esportra.Api.ScheduledJobs;

public sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    internal const string CookieName = "__hf_key";

    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        var env = httpContext.RequestServices.GetService<IHostEnvironment>();
        if (env?.IsDevelopment() == true)
            return true;

        var config = httpContext.RequestServices.GetService<IConfiguration>();
        var dashboardKey = config?["HangfireDashboardKey"];
        if (!string.IsNullOrEmpty(dashboardKey))
        {
            // First visit: ?key= in query string → set a cookie so CSS/JS assets load too
            if (httpContext.Request.Query.TryGetValue("key", out var queryKey) &&
                string.Equals(dashboardKey, queryKey, StringComparison.Ordinal))
            {
                httpContext.Response.Cookies.Append(CookieName, dashboardKey, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    MaxAge = TimeSpan.FromHours(12)
                });
                return true;
            }

            // Subsequent requests (CSS, JS, fonts): read from cookie
            if (httpContext.Request.Cookies.TryGetValue(CookieName, out var cookieKey) &&
                string.Equals(dashboardKey, cookieKey, StringComparison.Ordinal))
            {
                return true;
            }
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
