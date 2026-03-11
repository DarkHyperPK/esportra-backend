using System.Text;
using Esportra.Api.Auth;
using Esportra.Api.BackgroundJobs;
using Esportra.Api.Endpoints;
using Esportra.Api.Hubs;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Esportra.Core.Audit;
using Esportra.Core.Bracket;
using Esportra.Core.Match;
using Esportra.Infrastructure.Database;
using Esportra.Infrastructure.Email;
using Esportra.Infrastructure.Integrations;
using Esportra.Infrastructure.Supabase;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.IdentityModel.Tokens;

Console.WriteLine("[STARTUP] Creating builder...");
var builder = WebApplication.CreateBuilder(args);

// Explicitly configure Kestrel to bind on all interfaces, port 8080.
// Using ConfigureKestrel instead of UseUrls to bypass URL override logic.
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(8080);
});

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

        // Supabase JWT via Authorization header (standard) or query string (SignalR WS)
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
                ctx.HttpContext.Response.Headers["X-Auth-Error"] = ctx.Exception.GetType().Name;
                return Task.CompletedTask;
            },
        };
    });

// ── Authorization — policy per permission ─────────────────────────────────────
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddAuthorization(opts =>
{
    foreach (var perm in typeof(Permissions)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!))
    {
        opts.AddPolicy(perm, policy =>
            policy.Requirements.Add(new PermissionRequirement(perm)));
    }

    opts.AddPolicy("Organizer",    policy => policy.RequireAuthenticatedUser());
    opts.AddPolicy("VenueOwner",   policy => policy.RequireAuthenticatedUser());
    opts.AddPolicy("Authenticated", policy => policy.RequireAuthenticatedUser());
});

// ── Database ──────────────────────────────────────────────────────────────────
var pgConnStr = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
builder.Services.AddSingleton<IDbConnectionFactory>(new NpgsqlConnectionFactory(pgConnStr));

// ── Redis + HybridCache ────────────────────────────────────────────────────────
var redisConnStr = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
Console.WriteLine($"[STARTUP] Redis connection: {redisConnStr.Split(',')[0]}...");

// DIAGNOSTIC: Temporarily use in-memory cache to isolate if Redis blocks startup.
// If Kestrel binds with this, Redis is the problem. Remove after diagnosis.
var useRedis = Environment.GetEnvironmentVariable("USE_REDIS") != "false";
if (useRedis)
{
    var redisConfigOptions = StackExchange.Redis.ConfigurationOptions.Parse(redisConnStr);
    redisConfigOptions.AbortOnConnectFail = false;
    redisConfigOptions.ConnectTimeout = 5000;
    redisConfigOptions.SyncTimeout = 5000;

    builder.Services.AddStackExchangeRedisCache(opts =>
    {
        opts.ConfigurationOptions = redisConfigOptions;
        opts.InstanceName  = "esportra:";
    });
    Console.WriteLine("[STARTUP] Using Redis distributed cache");
}
else
{
    builder.Services.AddDistributedMemoryCache();
    Console.WriteLine("[STARTUP] Using IN-MEMORY distributed cache (diagnostic mode)");
}

builder.Services.AddHybridCache(opts =>
{
    opts.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration           = TimeSpan.FromSeconds(60),
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
              .AllowCredentials());
});

// ── Email service (Resend) ────────────────────────────────────────────────────
builder.Services.AddHttpClient<ResendEmailService>(http =>
{
    http.DefaultRequestHeaders.Add("Authorization",
        $"Bearer {builder.Configuration["Resend:ApiKey"]}");
});
builder.Services.AddScoped<IEmailService, ResendEmailService>();

// ── Supabase Admin client ─────────────────────────────────────────────────────
builder.Services.AddHttpClient<SupabaseAdminClient>();
builder.Services.AddScoped<ISupabaseAdminClient, SupabaseAdminClient>();

// ── External API clients ──────────────────────────────────────────────────────
builder.Services.AddHttpClient<RiotApiClient>();
builder.Services.AddHttpClient<FaceitApiClient>();
builder.Services.AddHttpClient<RawgApiClient>();

// Generic HttpClient for use in endpoints (Riot/Faceit OAuth flows)
builder.Services.AddHttpClient();

// Named GeoIP client for MetricEndpoints
builder.Services.AddHttpClient("GeoIP", http =>
{
    http.Timeout = TimeSpan.FromSeconds(3);
});

// ── Phase 2: Core services ────────────────────────────────────────────────────
builder.Services.AddScoped<BracketPersistenceService>();
builder.Services.AddScoped<StandingsService>();
builder.Services.AddScoped<SwissNextRoundService>();
builder.Services.AddScoped<VetoDbService>();
builder.Services.AddScoped<AuditService>();

// ── Background jobs ───────────────────────────────────────────────────────────
builder.Services.AddHostedService<AutomatedRemindersJob>();

// ── OpenAPI ────────────────────────────────────────────────────────────────────
builder.Services.AddOpenApi();

// ═════════════════════════════════════════════════════════════════════════════
Console.WriteLine("[STARTUP] Building app...");
var app = builder.Build();
Console.WriteLine("[STARTUP] App built successfully.");
// ═════════════════════════════════════════════════════════════════════════════

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// ── Proxy / forwarded-header support ──────────────────────────────────────────
// Coolify (and any nginx reverse proxy) terminates TLS and forwards HTTP to
// the container. Trust the X-Forwarded-For / X-Forwarded-Proto headers so
// that OAuth redirects and HTTPS detection work correctly.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // Trust all proxies inside Docker network (Coolify sets up a Docker network).
    KnownNetworks  = { },
    KnownProxies   = { },
});

// ── Health — mapped first so it always responds, even if other middleware fails
app.MapGet("/health", () => Results.Ok(new
{
    status    = "healthy",
    timestamp = DateTime.UtcNow,
    version   = "1.0.0-phase4",
}));

app.UseCors("EsportraPolicy");
app.UseRouting();
app.UseAuthentication();
app.UseRoleEnrichment();   // Enrich JWT → DB roles + permissions
app.UseRateLimit();        // Redis sliding-window rate limiter
app.UseAuthorization();

// Map UnauthorizedAccessException (thrown by AssertCaptain/AssertOwner) → 403
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (UnauthorizedAccessException ex)
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

// ── JWT validation probe ───────────────────────────────────────────────────────
app.MapGet("/api/me", (HttpContext ctx) =>
{
    var userCtx = ctx.Items["UserContext"] as UserContext;
    return userCtx is null ? Results.Unauthorized() : Results.Ok(userCtx);
}).RequireAuthorization("Authenticated");

// ── Phase 1: Edge Function replacements ───────────────────────────────────────
app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapIntegrationEndpoints();
app.MapGameEndpoints();
app.MapMetricEndpoints();
app.MapMatchEndpoints();
app.MapBracketEndpoints();

// ── Phase 4: Domain API endpoints ─────────────────────────────────────────────
app.MapProfileEndpoints();
app.MapProfileResolveEndpoint();
app.MapMatchSystemEndpoints();
app.MapTeamEndpoints();
app.MapTournamentEndpoints();
app.MapVenueEndpoints();
app.MapOrganizationEndpoints();
app.MapNotificationEndpoints();
app.MapStageEndpoints();
app.MapReviewEndpoints();
app.MapOrganizerEndpoints();
app.MapPartnerEndpoints();
app.MapLeaderboardEndpoints();
app.MapMessagingEndpoints();
app.MapVetoEndpoints();
app.MapAnalyticsEndpoints();

// ── Phase 3: SignalR hubs ──────────────────────────────────────────────────────
app.MapHub<BracketHub>("/hubs/bracket");
app.MapHub<MatchHub>("/hubs/match");
app.MapHub<VetoHub>("/hubs/veto");
app.MapHub<ChatHub>("/hubs/chat");
app.MapHub<ConversationHub>("/hubs/conversations");
app.MapHub<NotificationHub>("/hubs/notifications");
app.MapHub<LiveHub>("/hubs/live");

Console.WriteLine("[STARTUP] Pipeline configured. Starting app...");
Console.Out.Flush();

// Diagnostics: know definitively if host completes startup
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine("[STARTUP] ✅ APPLICATION STARTED — Kestrel is listening!");
    Console.Out.Flush();
});
app.Lifetime.ApplicationStopping.Register(() =>
{
    Console.WriteLine("[STARTUP] ⚠️ APPLICATION STOPPING!");
    Console.Out.Flush();
});

// List hosted services for diagnostics
var hostedServices = app.Services.GetServices<IHostedService>().ToList();
Console.WriteLine($"[STARTUP] {hostedServices.Count} hosted services registered:");
foreach (var svc in hostedServices)
    Console.WriteLine($"  - {svc.GetType().FullName}");
Console.Out.Flush();

// Heartbeat timer — proves the process is alive while waiting for startup
var startWatch = System.Diagnostics.Stopwatch.StartNew();
using var heartbeat = new Timer(_ =>
{
    Console.WriteLine($"[HEARTBEAT] Process alive at {startWatch.Elapsed.TotalSeconds:F0}s — startup NOT complete");
    Console.Out.Flush();
}, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));

try
{
    // Probe DI resolution of key services — any hang here points to the culprit
Console.WriteLine("[STARTUP] Probing DI resolution...");
Console.Out.Flush();
try
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var dc = app.Services.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();
    Console.WriteLine($"[STARTUP]   IDistributedCache -> {dc?.GetType().Name} ({sw.ElapsedMilliseconds}ms)");
    var hc = app.Services.GetService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();
    Console.WriteLine($"[STARTUP]   HybridCache -> {hc?.GetType().Name} ({sw.ElapsedMilliseconds}ms)");
    Console.Out.Flush();
}
catch (Exception ex)
{
    Console.WriteLine($"[STARTUP]   DI probe FAILED: {ex.Message}");
    Console.Out.Flush();
}

Console.WriteLine("[STARTUP] Calling app.StartAsync()...");
    Console.Out.Flush();
    await app.StartAsync();
    Console.WriteLine($"[STARTUP] ✅ StartAsync completed in {startWatch.Elapsed.TotalSeconds:F1}s");
    Console.Out.Flush();
}
catch (Exception ex)
{
    Console.WriteLine($"[STARTUP] ❌ StartAsync THREW at {startWatch.Elapsed.TotalSeconds:F1}s: {ex}");
    Console.Out.Flush();
    throw;
}

heartbeat.Dispose();
await app.WaitForShutdownAsync();
