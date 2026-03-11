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
Console.WriteLine($"[STARTUP] Redis connection string: {redisConnStr.Split(',')[0]}...");

// Parse and configure Redis with strict timeouts and non-blocking connect.
var redisConfig = StackExchange.Redis.ConfigurationOptions.Parse(redisConnStr);
redisConfig.AbortOnConnectFail = false;
redisConfig.ConnectTimeout = 5000;
redisConfig.SyncTimeout = 3000;
redisConfig.AsyncTimeout = 5000;
// If SSL is requested, don't validate the certificate (self-signed in Docker)
if (redisConfig.Ssl)
    redisConfig.CertificateValidation += (_, _, _, _) => true;

Console.WriteLine($"[STARTUP] Redis config: ssl={redisConfig.Ssl}, endpoints={string.Join(",", redisConfig.EndPoints)}");
Console.Out.Flush();

// Create multiplexer eagerly but with AbortOnConnectFail=false — returns immediately
// even if Redis is unreachable. Operations will fail gracefully until connected.
StackExchange.Redis.IConnectionMultiplexer? redisMux = null;
try
{
    redisMux = StackExchange.Redis.ConnectionMultiplexer.Connect(redisConfig);
    Console.WriteLine($"[STARTUP] Redis multiplexer created (connected={redisMux.IsConnected})");
}
catch (Exception ex)
{
    Console.WriteLine($"[STARTUP] Redis connect failed (will use in-memory fallback): {ex.Message}");
}
Console.Out.Flush();

if (redisMux != null)
{
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(redisMux);
    builder.Services.AddStackExchangeRedisCache(opts =>
    {
        opts.ConnectionMultiplexerFactory = () => Task.FromResult(redisMux);
        opts.InstanceName = "esportra:";
    });
    Console.WriteLine("[STARTUP] Using Redis distributed cache");
}
else
{
    builder.Services.AddDistributedMemoryCache();
    Console.WriteLine("[STARTUP] Using in-memory distributed cache (Redis unavailable)");
}
Console.Out.Flush();

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

// Heartbeat: confirm process stays alive during GenericWebHostService.StartAsync()
var _sw = System.Diagnostics.Stopwatch.StartNew();
using var _hb = new Timer(_ =>
{
    Console.WriteLine($"[HEARTBEAT] alive {_sw.Elapsed.TotalSeconds:F0}s, waiting for Kestrel...");
    Console.Out.Flush();
}, null, 5000, 10000);

try
{
    using var _cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    Console.WriteLine("[STARTUP] Calling StartAsync (30s timeout)...");
    Console.Out.Flush();
    await app.StartAsync(_cts.Token);
    Console.WriteLine($"[STARTUP] ✅ StartAsync done in {_sw.Elapsed.TotalSeconds:F1}s");
    Console.Out.Flush();
    _hb.Dispose();
    await app.WaitForShutdownAsync();
}
catch (OperationCanceledException)
{
    Console.WriteLine($"[STARTUP] ❌ StartAsync TIMED OUT after {_sw.Elapsed.TotalSeconds:F0}s!");
    Console.Out.Flush();
    await Task.Delay(Timeout.Infinite); // keep alive so we can inspect
}
catch (Exception ex)
{
    Console.WriteLine($"[STARTUP] ❌ FATAL: {ex}");
    Console.Out.Flush();
    throw;
}
