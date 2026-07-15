using Esportra.Api.Services;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Middleware;

public sealed class AdminMutationAuditMiddleware(
    RequestDelegate next,
    OperationsAuditService audit)
{
    private static readonly HashSet<string> MutatingMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsAuditedAdminMutation(context) &&
            context.Items["UserContext"] is UserContext userCtx)
        {
            await audit.WriteFromHttpAsync(
                context,
                userCtx,
                $"admin.{context.Request.Method.ToLowerInvariant()}.intent",
                "admin_route",
                context.Request.Path,
                new
                {
                    before = new { accepted = false },
                    after = new
                    {
                        accepted = true,
                        method = context.Request.Method,
                        path = context.Request.Path.Value,
                        query = context.Request.QueryString.Value
                    }
                },
                "medium",
                context.RequestAborted);
        }

        await next(context);
    }

    private static bool IsAuditedAdminMutation(HttpContext context) =>
        MutatingMethods.Contains(context.Request.Method) &&
        context.Request.Path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase);
}

public static class AdminMutationAuditMiddlewareExtensions
{
    public static IApplicationBuilder UseAdminMutationAudit(this IApplicationBuilder app)
        => app.UseMiddleware<AdminMutationAuditMiddleware>();
}
