using System.Text;
using Esportra.Api.Auth;
using Esportra.Api.BackgroundJobs;
using Esportra.Api.Endpoints;
using Esportra.Api.Services;
using Esportra.Api.HealthChecks;
using Esportra.Api.Helpers;
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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

Console.WriteLine("[STARTUP] Creating builder...");
var builder = WebApplication.CreateBuilder(args);

// Configure JSON serialization: camelCase output (default), case-insensitive input
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

// Enable Dapper snake_case → PascalCase mapping for typed record DTOs
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

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

// ── Authentication — Supabase JWT ─────────────────────────────────────────────
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
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
        options.Events = new JwtBearerEvents
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
                ctx.HttpContext.Response.Headers["X-Auth-Error"] = "authentication_failed";
                return Task.CompletedTask;
            },
        };
    });

// ── Authorization — policy per permission ─────────────────────────────────────
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, AdminHandler>();
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

    opts.AddPolicy("Authenticated", policy => policy.RequireAuthenticatedUser());
    opts.AddPolicy("Admin", policy =>
        policy.Requirements.Add(new AdminRequirement()));
});

// ── Database ──────────────────────────────────────────────────────────────────
var pgConnStr = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

// Log connection target and attempt DNS resolution for debugging
try
{
    var csb = new Npgsql.NpgsqlConnectionStringBuilder(pgConnStr);
    var pgHost = csb.Host;
    var pgPort = csb.Port;
    Console.WriteLine($"[STARTUP] Postgres target: {pgHost}:{pgPort}");

    try
    {
        var addresses = System.Net.Dns.GetHostAddresses(pgHost!);
        Console.WriteLine($"[STARTUP] Postgres DNS resolved: {string.Join(", ", addresses.Select(a => a.ToString()))}");
    }
    catch (Exception dnsEx)
    {
        Console.WriteLine($"[STARTUP] ⚠️ Postgres DNS FAILED for '{pgHost}': {dnsEx.Message}");
    }
}
catch { Console.WriteLine("[STARTUP] Postgres connection string configured (could not parse)."); }

builder.Services.AddSingleton<IDbConnectionFactory>(new NpgsqlConnectionFactory(pgConnStr));

// ── Redis + HybridCache ────────────────────────────────────────────────────────
// Supports two env var formats:
//   REDIS_URL=redis://user:password@host:port/db  (standard URL — preferred for Coolify)
//   ConnectionStrings__Redis=host:port,user=...,password=...  (SE.Redis format — fallback)
var redisConfig = BuildRedisConfig(builder.Configuration);
Console.WriteLine($"[STARTUP] Redis connection string: {redisConfig.EndPoints[0]}...");

static ConfigurationOptions BuildRedisConfig(IConfiguration config)
{
    var redisUrl = config["REDIS_URL"];
    if (!string.IsNullOrWhiteSpace(redisUrl))
    {
        // Parse redis://user:password@host:port/db
        var uri = new Uri(redisUrl);
        var opts = new ConfigurationOptions();
        opts.EndPoints.Add(uri.Host, uri.Port > 0 ? uri.Port : 6379);

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            if (parts.Length == 2)
            {
                opts.User     = Uri.UnescapeDataString(parts[0]);
                opts.Password = Uri.UnescapeDataString(parts[1]);
            }
            else
            {
                opts.Password = Uri.UnescapeDataString(parts[0]);
            }
        }

        // /0 → database 0, /1 → database 1, etc.
        if (uri.AbsolutePath.TrimStart('/') is { Length: > 0 } db && int.TryParse(db, out var dbIndex))
            opts.DefaultDatabase = dbIndex;

        return opts;
    }

    // Fallback to SE.Redis connection string format
    var connStr = config.GetConnectionString("Redis") ?? "localhost:6379";
    return ConfigurationOptions.Parse(connStr);
}

// AbortOnConnectFail=false means startup never blocks; SE.Redis reconnects automatically
// whenever Redis becomes available after a transient outage.
redisConfig.AbortOnConnectFail   = false;
redisConfig.ReconnectRetryPolicy = new LinearRetry(5_000);
redisConfig.ConnectTimeout       = 5_000;
redisConfig.SyncTimeout          = 3_000;
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConfig));

// Register a swappable proxy as IDistributedCache.
// It starts with an in-memory fallback; RedisBackgroundConnector calls
// SwappableDistributedCache.Swap() once a PING to Redis succeeds, so all
// consumers (RateLimitMiddleware, HybridCache L2) transparently switch to Redis.
var swappableCache = new SwappableDistributedCache();
builder.Services.AddSingleton(swappableCache);
builder.Services.AddSingleton<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(swappableCache);

builder.Services.AddHybridCache(opts =>
{
    opts.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration           = TimeSpan.FromSeconds(60),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };
});

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis",    tags: ["ready"])
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

// ── SignalR ────────────────────────────────────────────────────────────────────
builder.Services.AddSignalR(opts =>
{
    opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

// ── CORS ───────────────────────────────────────────────────────────────────────
static string[] ResolveAllowedOrigins(IConfiguration config)
{
    var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static IEnumerable<string> SplitOrigins(string raw) =>
        raw.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static void AddOrigin(HashSet<string> set, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        foreach (var rawOrigin in SplitOrigins(value))
        {
            if (!Uri.TryCreate(rawOrigin, UriKind.Absolute, out var uri)) continue;
            set.Add(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'));
        }
    }

    var originSection = config.GetSection("Cors:AllowedOrigins");
    foreach (var child in originSection.GetChildren())
        AddOrigin(origins, child.Value);

    AddOrigin(origins, config["Cors:AllowedOrigins"]);
    AddOrigin(origins, config["CORS_ALLOWED_ORIGINS"]);
    AddOrigin(origins, config["FrontendUrl"]);
    AddOrigin(origins, config["Frontend:BaseUrl"]);
    AddOrigin(origins, config["PartnerUrl"]);

    if (origins.Count == 0)
    {
        foreach (var fallback in new[]
        {
            "https://frontend-staging.esportra.com",
            "https://staging.esportra.com",
            "https://esportra.com",
            "https://www.esportra.com",
            "https://partner.esportra.com",
            "https://partners.esportra.com",
            "http://localhost:3000",
            "http://localhost:3001",
            "http://localhost:4173",
            "http://127.0.0.1:4173",
            "http://localhost:5173",
            "http://localhost:5174",
        })
        {
            AddOrigin(origins, fallback);
        }
    }

    return origins.ToArray();
}

var allowedOrigins = ResolveAllowedOrigins(builder.Configuration);
Console.WriteLine($"[STARTUP] CORS allowed origins: {string.Join(", ", allowedOrigins)}");

builder.Services.AddCors(opts =>
{
    opts.AddPolicy("EsportraPolicy", policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()
              .SetPreflightMaxAge(TimeSpan.FromHours(2)));
});

// ── Email service (SMTP via MailKit) ─────────────────────────────────────────
builder.Services.AddScoped<IEmailService, ResendEmailService>();

// ── Supabase Admin client ─────────────────────────────────────────────────────
builder.Services.AddHttpClient<SupabaseAdminClient>();
builder.Services.AddScoped<ISupabaseAdminClient, SupabaseAdminClient>();

// ── External API clients ──────────────────────────────────────────────────────
builder.Services.AddHttpClient<RiotApiClient>();
builder.Services.AddHttpClient<RawgApiClient>();
builder.Services.AddHttpClient<IgdbApiClient>();
builder.Services.AddHttpClient<IDatHostService, DatHostService>();

// ── Data Protection (OAuth state encryption) ─────────────────────────────────
builder.Services.AddDataProtection()
    .SetApplicationName("Esportra");
builder.Services.AddSingleton<OAuthStateProtector>();

// Generic HttpClient for use in endpoints (Riot OAuth flows)
builder.Services.AddHttpClient();

// Named GeoIP client for MetricEndpoints
builder.Services.AddHttpClient("GeoIP", http =>
{
    http.Timeout = TimeSpan.FromSeconds(3);
});

// Named VenueHub client for venue-hub inter-service calls
builder.Services.AddHttpClient("VenueHub", http =>
{
    http.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddSingleton<Esportra.Api.Services.VenueHubService>();
builder.Services.AddSingleton<Esportra.Api.Services.VenueConnectionTracker>();

// ── Phase 2: Core services ────────────────────────────────────────────────────
builder.Services.AddScoped<BracketPersistenceService>();
builder.Services.AddScoped<MatchFinalizationService>();
builder.Services.AddScoped<StandingsService>();
builder.Services.AddScoped<SwissNextRoundService>();
builder.Services.AddScoped<VetoDbService>();
builder.Services.AddScoped<MatchScheduleNotificationService>();
builder.Services.AddScoped<BrScheduleNotificationService>();
builder.Services.AddScoped<Esportra.Core.Tournaments.SelfPlayMatchRoomService>();
builder.Services.AddScoped<Esportra.Core.Tournaments.CheckinWalkoverProcessor>();
builder.Services.AddScoped<Esportra.Api.Services.CheckinWalkoverNotifier>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<OperationsAuditService>();
builder.Services.AddScoped<GhostModeTokenService>();
builder.Services.AddScoped<OperationsAuthorizationService>();
builder.Services.AddScoped<Esportra.Api.Services.GameCatalogService>();
builder.Services.AddScoped<Esportra.Api.Services.GameCatalogAssetService>();
builder.Services.AddScoped<Esportra.Api.Services.TournamentWinnerService>();
builder.Services.AddScoped<Esportra.Api.Services.BattleRoyaleStageBootstrapService>();
builder.Services.AddScoped<Esportra.Core.Alerts.AdminAlertService>();
builder.Services.AddScoped<Esportra.Api.Services.BillingService>();
builder.Services.AddScoped<IStaffAuthorizationService, StaffAuthorizationService>();
builder.Services.AddScoped<Esportra.Api.Services.TournamentAuthorizationService>();

// ── Discord bot DM notifications ──────────────────────────────────────────────
builder.Services.AddHttpClient("Discord");
builder.Services.AddSingleton<Esportra.Api.Services.DiscordNotificationService>();

// ── Background jobs ───────────────────────────────────────────────────────────
builder.Services.AddHostedService<RedisBackgroundConnector>();
builder.Services.AddHostedService<CheckinWalkoversJob>();
builder.Services.AddHostedService<DiscordDmDispatcherJob>();
builder.Services.AddHostedService<InviteExpiryJob>();
builder.Services.AddHostedService<PublicVetoCleanupJob>();
builder.Services.AddHostedService<R6MapAssetSeedService>();

// ── OpenAPI ────────────────────────────────────────────────────────────────────
builder.Services.AddOpenApi();

// ═════════════════════════════════════════════════════════════════════════════
Console.WriteLine("[STARTUP] Building app...");
var app = builder.Build();
Console.WriteLine("[STARTUP] App built successfully.");

// ── Run database migrations (development/legacy fallback only) ──────────────
// Dedicated schema upgrades should run through Esportra.Migrator before the API
// is rolled out. The app only keeps startup migrations for local development
// and explicit legacy opt-in until deployment flow is fully aligned.
var runMigrationsOnStartup =
    builder.Environment.IsDevelopment()
    || builder.Configuration.GetValue("Database:RunMigrationsOnStartup", false);

if (runMigrationsOnStartup)
{
    // Migrations need supabase_admin (superuser) to issue GRANTs on tables it owns.
    // postgres user is NOT superuser in Supabase and GRANT silently no-ops.
    // Fallback to the regular connection string if no migration-specific one is set.
    var migrationConnStr = builder.Configuration.GetConnectionString("PostgresMigrations") ?? pgConnStr;
    var migrationLogger = app.Services.GetRequiredService<ILogger<Esportra.Infrastructure.Migrations.MigrationRunner>>();
    var migrationRunner = new Esportra.Infrastructure.Migrations.MigrationRunner(migrationConnStr, migrationLogger);
    if (!migrationRunner.Run())
    {
        Console.Error.WriteLine("[STARTUP] Database migration failed. Aborting.");
        Environment.Exit(1);
    }
}
else
{
    // Phase 3: schema compatibility gate.
    // When the dedicated migrator is responsible for schema upgrades, the API still
    // verifies at startup that all embedded migration scripts have been applied.
    // If the database is behind the code, the API refuses to start so a partially
    // migrated database never serves requests.
    Console.WriteLine("[STARTUP] Checking schema compatibility (dedicated migrator mode)...");
    var compatCheckConnStr = builder.Configuration.GetConnectionString("PostgresMigrations") ?? pgConnStr;
    var compatLogger = app.Services.GetRequiredService<ILogger<Esportra.Infrastructure.Migrations.MigrationRunner>>();
    var compatRunner = new Esportra.Infrastructure.Migrations.MigrationRunner(compatCheckConnStr, compatLogger);
    var compat = compatRunner.CheckCompatibility();
    if (!compat.IsCompatible)
    {
        Console.Error.WriteLine(
            $"[STARTUP] ❌ Schema incompatibility: {compat.PendingScripts.Count} pending migration(s).");
        Console.Error.WriteLine(
            "[STARTUP] ❌ Run 'Esportra.Migrator' before starting the API.");
        foreach (var script in compat.PendingScripts)
            Console.Error.WriteLine($"[STARTUP]    - {script}");
        Environment.Exit(1);
    }
    Console.WriteLine("[STARTUP] ✅ Schema compatibility check passed.");
}
// ═════════════════════════════════════════════════════════════════════════════

// ── Initialize email templates with environment-aware URLs ────────────────────
Esportra.Infrastructure.Email.EmailTemplates.Init(
    builder.Configuration["FrontendUrl"] ?? "https://esportra.com",
    builder.Configuration["Supabase:Url"] ?? "https://api.esportra.com");

using (var scope = app.Services.CreateScope())
{
    var catalog = scope.ServiceProvider.GetRequiredService<Esportra.Api.Services.GameCatalogService>();
    await catalog.ImportPackagedCatalogAsync();
    await catalog.BackfillActiveCatalogBannerUrlsAsync();
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// ── Proxy / forwarded-header support ──────────────────────────────────────────
// Coolify (and any nginx reverse proxy) terminates TLS and forwards HTTP to
// the container. Trust the X-Forwarded-For / X-Forwarded-Proto headers so
// that OAuth redirects and HTTPS detection work correctly.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
    // Trust Docker bridge network and typical Coolify/Traefik subnets
    KnownNetworks  = { new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("172.16.0.0"), 12) },
    KnownProxies   = { },
});

// ── Health probes — mapped before middleware so they always respond ────────────
// /health/live  — liveness: is the process up? (Coolify/k8s restart probe)
// /health/ready — readiness: are Postgres + Redis reachable? (Coolify startup probe)
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate      = _ => false,     // no checks — pure liveness ping
    ResponseWriter = HealthResponseWriter.WriteJson,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate      = c => c.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteJson,
}).AllowAnonymous();

// Legacy /health kept for backwards-compat with existing Coolify health check config
app.MapGet("/health", () => Results.Ok(new
{
    status    = "healthy",
    timestamp = DateTime.UtcNow,
    version   = "1.0.0-phase4",
    build     = "20260328-rbac-fix",
}));

app.UseRouting();
app.UseCors("EsportraPolicy");
app.UseStaticFiles();  // Serve wwwroot/ (email templates, etc.)

// Global exception handler — placed right after CORS so error responses keep
// Access-Control-Allow-Origin headers instead of being swallowed as "CORS blocked".
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (UnauthorizedAccessException)
    {
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = "You don't have permission to perform this action." });
        }
    }
    catch (Exception ex)
    {
        var logger = ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("GlobalExceptionHandler");
        logger?.LogError(ex, "Unhandled exception on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
        if (!ctx.Response.HasStarted)
        {
            var env = ctx.RequestServices.GetRequiredService<IHostEnvironment>();
            var payload = ApiErrorResponses.FromException(ex, ctx.Request.Path, !env.IsProduction());
            ctx.Response.StatusCode = payload.StatusCode;
            await ctx.Response.WriteAsJsonAsync(ApiErrorResponses.ToJson(payload, !env.IsProduction()));
        }
    }
});
app.UseAuthentication();
app.UseRoleEnrichment();   // Enrich JWT → DB roles + permissions
app.UseSuspensionGate();   // Block suspended users (allowlist /api/profiles/me)
app.UseGhostMode();        // Validate and audit short-lived impersonation tokens
app.UseAdminMutationAudit(); // Pre-audit destructive admin mutations before endpoint execution
app.UseRateLimit();        // Redis sliding-window rate limiter
app.UseAuthorization();

// ── JWT validation probe───────────────────────────────────────────────────────
app.MapGet("/api/me", (HttpContext ctx) =>
{
    var userCtx = ctx.Items["UserContext"] as UserContext;
    return userCtx is null ? Results.Unauthorized() : Results.Ok(userCtx);
}).RequireAuthorization("Authenticated");

// ── Phase 1: Edge Function replacements ───────────────────────────────────────
app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapOperationsEndpoints();
app.MapIntegrationEndpoints();
app.MapSteamAccountEndpoints();
app.MapGameEndpoints();
app.MapGameCatalogAdminEndpoints();
app.MapMetricEndpoints();
app.MapMatchEndpoints();
app.MapBracketEndpoints();
app.MapPublicToolEndpoints();

// ── Phase 4: Domain API endpoints ─────────────────────────────────────────────
app.MapProfileEndpoints();
app.MapProfileResolveEndpoint();
app.MapMatchSystemEndpoints();
app.MapTeamEndpoints();
app.MapTournamentEndpoints();
app.MapTournamentInvitationEndpoints();
app.MapVenueEndpoints();
app.MapVenueStaffEndpoints();
app.MapSessionRefundEndpoints();
app.MapOrganizationEndpoints();
app.MapNotificationEndpoints();
app.MapStageEndpoints();
app.MapBRGroupEndpoints();
app.MapReviewEndpoints();
app.MapOrganizerEndpoints();
app.MapPartnerEndpoints();
app.MapLeaderboardEndpoints();
app.MapMessagingEndpoints();
app.MapVetoEndpoints();
app.MapAnalyticsEndpoints();
app.MapStorageEndpoints();
app.MapSponsorEndpoints();
app.MapTournamentSponsorEndpoints();
app.MapSitemapEndpoints();
app.MapWalletEndpoints();
app.MapLoyaltyEndpoints();
app.MapAnnouncementEndpoints();
app.MapComboEndpoints();
app.MapGameServerEndpoints();
app.MapMatchZyEndpoints();
app.MapSessionEndpoints();
app.MapZoneEndpoints();
app.MapMemberEndpoints();
app.MapBookingRulesEndpoints();
app.MapWalkInEndpoints();
app.MapPOSEndpoints();
app.MapPackageEndpoints();
app.MapVenueAnalyticsEndpoints();
app.MapStaffPermissionEndpoints();
app.MapNotificationPreferenceEndpoints();

// ── Phase 3: SignalR hubs──────────────────────────────────────────────────────
app.MapHub<BracketHub>("/hubs/bracket").RequireCors("EsportraPolicy");
app.MapHub<MatchHub>("/hubs/match").RequireCors("EsportraPolicy");
app.MapHub<VetoHub>("/hubs/veto").RequireCors("EsportraPolicy");
app.MapHub<ChatHub>("/hubs/chat").RequireCors("EsportraPolicy");
app.MapHub<ConversationHub>("/hubs/conversations").RequireCors("EsportraPolicy");
app.MapHub<NotificationHub>("/hubs/notifications").RequireCors("EsportraPolicy");
app.MapHub<LiveHub>("/hubs/live").RequireCors("EsportraPolicy");
app.MapHub<VenueSyncHub>("/hubs/venue-sync").RequireCors("EsportraPolicy");
app.MapHub<BRHub>("/hubs/br").RequireCors("EsportraPolicy");

Console.WriteLine("[STARTUP] Pipeline configured. Starting app...");
Console.Out.Flush();

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine("[STARTUP] ✅ APPLICATION STARTED — Kestrel is listening!");

    // Probe Postgres connectivity on startup so issues show in deploy logs
    try
    {
        using var conn = app.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        cmd.ExecuteScalar();
        Console.WriteLine("[STARTUP] ✅ Postgres: connected and responding.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[STARTUP] ❌ Postgres: {ex.GetType().Name} — {ex.Message}");
    }

    Console.Out.Flush();
});

try
{
    await app.RunAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"[STARTUP] ❌ FATAL: {ex}");
    Console.Out.Flush();
    throw;
}

