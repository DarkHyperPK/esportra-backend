using System.Security.Claims;
using System.Text.Encodings.Web;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Esportra.Api.Tests.Infrastructure;

public sealed class EsportraWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public EsportraWebApplicationFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            var descriptors = services
                .Where(d => d.ServiceType == typeof(IDbConnectionFactory))
                .ToList();
            foreach (var d in descriptors) services.Remove(d);
            services.AddSingleton<IDbConnectionFactory>(new NpgsqlConnectionFactory(_connectionString));

            services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);


            var hostedServices = services
                .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                    && d.ImplementationType?.Name is not "HealthCheckPublisherHostedService")
                .ToList();
            foreach (var d in hostedServices) services.Remove(d);
        });
    }

    public HttpClient CreateAuthenticatedClient(Guid userId, bool isSuperAdmin = false, params string[] permissions)
    {
        var client = CreateClient();
        var claims = string.Join(",", permissions);
        client.DefaultRequestHeaders.Add("X-Test-UserId", userId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-SuperAdmin", isSuperAdmin.ToString());
        if (permissions.Length > 0)
            client.DefaultRequestHeaders.Add("X-Test-Permissions", claims);
        return client;
    }
}

public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userIdHeader = Context.Request.Headers["X-Test-UserId"].FirstOrDefault();
        if (string.IsNullOrEmpty(userIdHeader))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userIdHeader),
            new("sub", userIdHeader),
        };

        var isSuperAdmin = Context.Request.Headers["X-Test-SuperAdmin"].FirstOrDefault() == "True";
        if (isSuperAdmin)
            claims.Add(new Claim("role", "super_admin"));

        var permissions = Context.Request.Headers["X-Test-Permissions"].FirstOrDefault();
        if (!string.IsNullOrEmpty(permissions))
        {
            foreach (var perm in permissions.Split(','))
                claims.Add(new Claim("permission", perm));
        }

        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

