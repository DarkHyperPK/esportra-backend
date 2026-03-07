using System.Text;
using Esportra.Api.Auth;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ── Supabase JWT configuration ────────────────────────────────────────────────
var jwtSecret = builder.Configuration["Supabase:JwtSecret"]
    ?? throw new InvalidOperationException("Supabase:JwtSecret is required.");
var jwtAudience = builder.Configuration["Supabase:JwtAudience"] ?? "authenticated";
var jwtIssuer   = builder.Configuration["Supabase:JwtIssuer"];
var validateIssuer = !string.IsNullOrWhiteSpace(jwtIssuer);

// ── Authentication — Supabase HS256 JWT ───────────────────────────────────────
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer           = validateIssuer,
            ValidIssuer              = validateIssuer ? jwtIssuer : null,
            ValidateAudience         = true,
            ValidAudience            = jwtAudience,
            ValidateLifetime         = true,
            ClockSkew                = TimeSpan.Zero,
        };

        // Supabase passes the token as a Bearer header — standard flow.
        // For SignalR WS connections, also accept via query string.
        opts.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = ctx =>
            {
                ctx.HttpContext.Response.Headers["X-Auth-Error"] =
                    ctx.Exception.GetType().Name;
                return Task.CompletedTask;
            },
        };
    });

// ── Authorization — policies per permission ───────────────────────────────────
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddAuthorization(opts =>
{
    // Register a policy for every permission constant
    foreach (var perm in typeof(Permissions)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!))
    {
        opts.AddPolicy(perm, policy =>
            policy.Requirements.Add(new PermissionRequirement(perm)));
    }

    // Convenience policies for platform roles
    opts.AddPolicy("Organizer",    policy => policy.RequireAuthenticatedUser());
    opts.AddPolicy("VenueOwner",   policy => policy.RequireAuthenticatedUser());
    opts.AddPolicy("Authenticated", policy => policy.RequireAuthenticatedUser());
});

// ── Database — Npgsql connection factory ──────────────────────────────────────
var pgConnStr = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
builder.Services.AddSingleton<IDbConnectionFactory>(
    new NpgsqlConnectionFactory(pgConnStr));

// ── Redis + HybridCache ────────────────────────────────────────────────────────
var redisConnStr = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddStackExchangeRedisCache(opts =>
{
    opts.Configuration = redisConnStr;
    opts.InstanceName  = "esportra:";
});
builder.Services.AddHybridCache(opts =>
{
    opts.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration        = TimeSpan.FromSeconds(60),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };
});

// ── SignalR ────────────────────────────────────────────────────────────────────
builder.Services.AddSignalR(opts =>
{
    opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

// ── CORS ───────────────────────────────────────────────────────────────────────
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? ["http://localhost:5173"];

builder.Services.AddCors(opts =>
{
    opts.AddPolicy("EsportraPolicy", policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()); // required for SignalR
});

// ── OpenAPI ────────────────────────────────────────────────────────────────────
builder.Services.AddOpenApi();

// ═════════════════════════════════════════════════════════════════════════════
var app = builder.Build();
// ═════════════════════════════════════════════════════════════════════════════

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseCors("EsportraPolicy");
app.UseRouting();
app.UseAuthentication();
app.UseRoleEnrichment();   // Enrich JWT claims with DB roles + permissions
app.UseAuthorization();

// ── Health check ───────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new
{
    status    = "healthy",
    timestamp = DateTime.UtcNow,
    version   = "1.0.0",
}));

// ── Phase 0 validation endpoint ───────────────────────────────────────────────
// Returns the calling user's enriched context — used to verify JWT + role pipeline.
app.MapGet("/api/me", (HttpContext ctx) =>
{
    var userCtx = ctx.Items["UserContext"] as UserContext;
    return userCtx is null
        ? Results.Unauthorized()
        : Results.Ok(userCtx);
}).RequireAuthorization("Authenticated");

app.Run();
