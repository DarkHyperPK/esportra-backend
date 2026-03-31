using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Requests;
using Esportra.Infrastructure.Database;
using Esportra.Infrastructure.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// BFF (Backend-for-Frontend) OAuth integration endpoints.
/// All OAuth flows are handled server-side — the frontend never touches
/// authorization codes, tokens, client secrets, or PKCE verifiers.
/// </summary>
public static class IntegrationEndpoints
{
    private static string ResolveFrontendUrl(IConfiguration config) =>
        config["FrontendUrl"]
        ?? config["Frontend:BaseUrl"]
        ?? config.GetSection("Cors:AllowedOrigins").Get<string[]>()?.FirstOrDefault()
        ?? "http://localhost:5173";

    private static string ResolveBackendUrl(IConfiguration config) =>
        config["BackendUrl"] ?? config["Backend:BaseUrl"] ?? "http://localhost:5200";

    public static void MapIntegrationEndpoints(this WebApplication app)
    {
        // =====================================================================
        //  RIOT  —  BFF OAuth
        // =====================================================================

        // ── GET /api/integrations/riot/start ──────────────────────────────────
        // Authenticated. Generates encrypted state, returns Riot authorize URL.
        app.MapGet("/api/integrations/riot/start", (
            HttpContext           ctx,
            IConfiguration        config,
            OAuthStateProtector   stateProtector) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");
            if (userId is null) return Results.Unauthorized();

            var clientId    = config["Riot:OAuthClientId"] ?? string.Empty;
            var backendUrl  = ResolveBackendUrl(config);
            var redirectUri = $"{backendUrl}/api/integrations/riot/callback";

            var state = stateProtector.Protect(new OAuthStatePayload(
                UserId:       userId,
                Provider:     "riot",
                CodeVerifier: null,
                CreatedAt:    DateTime.UtcNow));

            var url = $"https://auth.riotgames.com/authorize"
                    + $"?redirect_uri={Uri.EscapeDataString(redirectUri)}"
                    + $"&client_id={clientId}"
                    + $"&response_type=code"
                    + $"&scope=openid"
                    + $"&state={Uri.EscapeDataString(state)}"
                    + $"&prompt=login";

            return Results.Ok(new { url });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/integrations/riot/callback ──────────────────────────────
        // Public (Riot redirects here). Decrypts state, exchanges code,
        // saves tokens, redirects browser to frontend.
        app.MapGet("/api/integrations/riot/callback", async (
            HttpContext           ctx,
            IConfiguration        config,
            OAuthStateProtector   stateProtector,
            IDbConnectionFactory  db,
            HttpClient            http,
            CancellationToken     ct) =>
        {
            var frontendUrl = ResolveFrontendUrl(config);
            var code  = ctx.Request.Query["code"].FirstOrDefault();
            var state = ctx.Request.Query["state"].FirstOrDefault();
            var error = ctx.Request.Query["error"].FirstOrDefault();

            if (!string.IsNullOrEmpty(error))
                return Results.Redirect($"{frontendUrl}/account/settings?riot_linked=error&reason={Uri.EscapeDataString(error)}");

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return Results.Redirect($"{frontendUrl}/account/settings?riot_linked=error&reason=missing_params");

            // Decrypt & validate state
            var payload = stateProtector.Unprotect(state);
            if (payload is null || payload.Provider != "riot")
                return Results.Redirect($"{frontendUrl}/account/settings?riot_linked=error&reason=invalid_state");

            if (!Guid.TryParse(payload.UserId, out var userGuid))
                return Results.Redirect($"{frontendUrl}/account/settings?riot_linked=error&reason=invalid_user");

            var clientId     = config["Riot:OAuthClientId"]     ?? string.Empty;
            var clientSecret = config["Riot:OAuthClientSecret"] ?? string.Empty;
            var backendUrl   = ResolveBackendUrl(config);
            var redirectUri  = $"{backendUrl}/api/integrations/riot/callback";

            // Exchange code for tokens
            var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://auth.riotgames.com/token");
            tokenReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "authorization_code",
                ["code"]          = code,
                ["redirect_uri"]  = redirectUri,
                ["client_id"]     = clientId,
                ["client_secret"] = clientSecret,
            });

            var tokenRes = await http.SendAsync(tokenReq, ct);
            if (!tokenRes.IsSuccessStatusCode)
                return Results.Redirect($"{frontendUrl}/account/settings?riot_linked=error&reason=token_exchange_failed");

            var tokenBody = await tokenRes.Content.ReadAsStringAsync(ct);
            using var tokenDoc = JsonDocument.Parse(tokenBody);
            var accessToken  = tokenDoc.RootElement.GetProperty("access_token").GetString()!;
            var refreshToken = tokenDoc.RootElement.TryGetProperty("refresh_token", out var rt)
                ? rt.GetString() : null;
            var expiresIn    = tokenDoc.RootElement.TryGetProperty("expires_in", out var ei)
                ? ei.GetInt32() : 3600;
            var expiresAt    = DateTime.UtcNow.AddSeconds(expiresIn);

            // Fetch account info
            var infoReq = new HttpRequestMessage(HttpMethod.Get,
                "https://asia.api.riotgames.com/riot/account/v1/accounts/me");
            infoReq.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            var infoRes = await http.SendAsync(infoReq, ct);
            if (!infoRes.IsSuccessStatusCode)
                return Results.Redirect($"{frontendUrl}/account/settings?riot_linked=error&reason=account_info_failed");

            var infoBody = await infoRes.Content.ReadAsStringAsync(ct);
            using var infoDoc = JsonDocument.Parse(infoBody);
            var puuid    = infoDoc.RootElement.GetProperty("puuid").GetString()!;
            var gameName = infoDoc.RootElement.GetProperty("gameName").GetString()!;
            var tagLine  = infoDoc.RootElement.GetProperty("tagLine").GetString()!;

            using var conn = db.CreateConnection();

            // Guard: PUUID already linked to another user
            var existingUserId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT user_id FROM public.riot_accounts WHERE puuid = @puuid AND user_id != @userId",
                new { puuid, userId = userGuid });
            if (existingUserId is not null)
                return Results.Redirect($"{frontendUrl}/account/settings?riot_linked=error&reason=already_linked_to_another_user");

            // Upsert riot_accounts
            await conn.ExecuteAsync("""
                INSERT INTO public.riot_accounts
                    (user_id, puuid, game_name, tag_line, access_token, refresh_token, token_expires_at)
                VALUES (@userId, @puuid, @gameName, @tagLine, @accessToken, @refreshToken, @expiresAt)
                ON CONFLICT (user_id) DO UPDATE SET
                    puuid = EXCLUDED.puuid,
                    game_name = EXCLUDED.game_name,
                    tag_line = EXCLUDED.tag_line,
                    access_token = EXCLUDED.access_token,
                    refresh_token = EXCLUDED.refresh_token,
                    token_expires_at = EXCLUDED.token_expires_at
                """, new { userId = userGuid, puuid, gameName, tagLine, accessToken, refreshToken, expiresAt });

            // Sync profiles.riot_tag
            await conn.ExecuteAsync(
                "UPDATE public.profiles SET riot_tag = @riotTag WHERE id = @userId",
                new { riotTag = $"{gameName}#{tagLine}", userId = userGuid });

            return Results.Redirect($"{frontendUrl}/account/settings?riot_linked=success");
        }); // Public — Riot redirect has no JWT

        // ── GET /api/integrations/riot ────────────────────────────────────────
        app.MapGet("/api/integrations/riot", async (
            IDbConnectionFactory db, HttpContext ctx, CancellationToken ct) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");
            if (userId is null) return Results.Unauthorized();
            if (!Guid.TryParse(userId, out var userGuid)) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var account = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT ra.puuid, ra.game_name, ra.tag_line, ra.region, ra.linked_at,
                       p.riot_tag
                FROM public.riot_accounts ra
                JOIN public.profiles p ON p.id = ra.user_id
                WHERE ra.user_id = @userId
                """, new { userId = userGuid });

            if (account is null) return Results.Ok(new { linked = false });
            return Results.Ok(new
            {
                linked    = true,
                puuid     = (string?)account.puuid,
                game_name = (string?)account.game_name,
                tag_line  = (string?)account.tag_line,
                region    = (string?)account.region,
                riot_tag  = (string?)account.riot_tag
            });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/integrations/riot ─────────────────────────────────────
        app.MapDelete("/api/integrations/riot", async (
            IDbConnectionFactory db, HttpContext ctx, CancellationToken ct) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");
            if (userId is null || !Guid.TryParse(userId, out var userGuid))
                return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM public.riot_accounts WHERE user_id = @userId", new { userId = userGuid });
            await conn.ExecuteAsync(
                "UPDATE public.profiles SET riot_tag = NULL WHERE id = @userId", new { userId = userGuid });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // =====================================================================
        //  FACEIT  —  BFF OAuth with PKCE (S256)
        // =====================================================================

        // ── GET /api/integrations/faceit/start ────────────────────────────────
        // Authenticated. Generates PKCE verifier + encrypted state, returns URL.
        app.MapGet("/api/integrations/faceit/start", (
            HttpContext           ctx,
            IConfiguration        config,
            OAuthStateProtector   stateProtector) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");
            if (userId is null) return Results.Unauthorized();

            var clientId    = config["Faceit:ClientId"] ?? string.Empty;
            var backendUrl  = ResolveBackendUrl(config);
            var redirectUri = $"{backendUrl}/api/integrations/faceit/callback";

            var codeVerifier  = OAuthStateProtector.GenerateCodeVerifier();
            var codeChallenge = OAuthStateProtector.ComputeCodeChallenge(codeVerifier);

            var state = stateProtector.Protect(new OAuthStatePayload(
                UserId:       userId,
                Provider:     "faceit",
                CodeVerifier: codeVerifier,
                CreatedAt:    DateTime.UtcNow));

            var url = $"https://accounts.faceit.com/"
                    + $"?client_id={clientId}"
                    + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                    + $"&response_type=code"
                    + $"&state={Uri.EscapeDataString(state)}"
                    + $"&code_challenge={codeChallenge}"
                    + $"&code_challenge_method=S256";

            return Results.Ok(new { url });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/integrations/faceit/callback ────────────────────────────
        // Public (Faceit redirects here). Decrypts state (with PKCE verifier),
        // exchanges code, saves tokens, redirects browser to frontend.
        app.MapGet("/api/integrations/faceit/callback", async (
            HttpContext           ctx,
            IConfiguration        config,
            OAuthStateProtector   stateProtector,
            IDbConnectionFactory  db,
            FaceitApiClient       faceit,
            HttpClient            http,
            CancellationToken     ct) =>
        {
            var frontendUrl = ResolveFrontendUrl(config);
            var code  = ctx.Request.Query["code"].FirstOrDefault();
            var state = ctx.Request.Query["state"].FirstOrDefault();
            var error = ctx.Request.Query["error"].FirstOrDefault();

            if (!string.IsNullOrEmpty(error))
                return Results.Redirect($"{frontendUrl}/settings?faceit_linked=error&reason={Uri.EscapeDataString(error)}");

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return Results.Redirect($"{frontendUrl}/settings?faceit_linked=error&reason=missing_params");

            // Decrypt & validate state
            var payload = stateProtector.Unprotect(state);
            if (payload is null || payload.Provider != "faceit" || payload.CodeVerifier is null)
                return Results.Redirect($"{frontendUrl}/settings?faceit_linked=error&reason=invalid_state");

            if (!Guid.TryParse(payload.UserId, out var userGuid))
                return Results.Redirect($"{frontendUrl}/settings?faceit_linked=error&reason=invalid_user");

            var backendUrl  = ResolveBackendUrl(config);
            var redirectUri = $"{backendUrl}/api/integrations/faceit/callback";

            // Exchange code for tokens (with PKCE verifier)
            var tokens = await faceit.ExchangeCodeAsync(
                code, payload.CodeVerifier, redirectUri, ct);
            if (tokens is null)
                return Results.Redirect($"{frontendUrl}/settings?faceit_linked=error&reason=token_exchange_failed");

            // Fetch account info
            var infoReq = new HttpRequestMessage(HttpMethod.Get,
                "https://api.faceit.com/auth/v1/resources/userinfo");
            infoReq.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.access_token);

            var infoRes = await http.SendAsync(infoReq, ct);
            if (!infoRes.IsSuccessStatusCode)
                return Results.Redirect($"{frontendUrl}/settings?faceit_linked=error&reason=account_info_failed");

            var infoBody = await infoRes.Content.ReadAsStringAsync(ct);
            using var doc  = JsonDocument.Parse(infoBody);
            var root       = doc.RootElement;
            var faceitId   = root.GetProperty("sub").GetString()!;
            var nickname   = root.TryGetProperty("nickname", out var nn) ? nn.GetString() : null;
            var avatarUrl  = root.TryGetProperty("picture",  out var av) ? av.GetString() : null;
            var expiresAt  = DateTime.UtcNow.AddSeconds(tokens.expires_in);

            using var conn = db.CreateConnection();

            // Guard: faceit_id already linked to another user
            var existingUserId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT user_id FROM public.faceit_accounts WHERE faceit_id = @faceitId AND user_id != @userId",
                new { faceitId, userId = userGuid });
            if (existingUserId is not null)
                return Results.Redirect($"{frontendUrl}/settings?faceit_linked=error&reason=already_linked_to_another_user");

            await conn.ExecuteAsync("""
                INSERT INTO public.faceit_accounts
                    (user_id, faceit_id, nickname, avatar_url, access_token, refresh_token, token_expires_at)
                VALUES (@userId, @faceitId, @nickname, @avatarUrl, @accessToken, @refreshToken, @expiresAt)
                ON CONFLICT (user_id) DO UPDATE SET
                    faceit_id = EXCLUDED.faceit_id,
                    nickname = EXCLUDED.nickname,
                    avatar_url = EXCLUDED.avatar_url,
                    access_token = EXCLUDED.access_token,
                    refresh_token = EXCLUDED.refresh_token,
                    token_expires_at = EXCLUDED.token_expires_at
                """, new { userId = userGuid, faceitId, nickname, avatarUrl,
                           accessToken = tokens.access_token,
                           refreshToken = tokens.refresh_token, expiresAt });

            await conn.ExecuteAsync(
                "UPDATE public.profiles SET faceit_nickname = @nickname WHERE id = @userId",
                new { nickname, userId = userGuid });

            return Results.Redirect($"{frontendUrl}/settings?faceit_linked=success");
        }); // Public — Faceit redirect has no JWT

        // ── GET /api/integrations/faceit ──────────────────────────────────────
        app.MapGet("/api/integrations/faceit", async (
            IDbConnectionFactory db, HttpContext ctx, CancellationToken ct) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");
            if (userId is null) return Results.Unauthorized();
            if (!Guid.TryParse(userId, out var userGuid)) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var account = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT fa.faceit_id, fa.nickname, fa.avatar_url, fa.linked_at,
                       p.faceit_nickname
                FROM public.faceit_accounts fa
                JOIN public.profiles p ON p.id = fa.user_id
                WHERE fa.user_id = @userId
                """, new { userId = userGuid });

            if (account is null) return Results.Ok(new { linked = false });
            return Results.Ok(new
            {
                linked          = true,
                faceit_id       = (string?)account.faceit_id,
                nickname        = (string?)account.nickname,
                avatar_url      = (string?)account.avatar_url,
                faceit_nickname = (string?)account.faceit_nickname
            });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/integrations/faceit ───────────────────────────────────
        app.MapDelete("/api/integrations/faceit", async (
            IDbConnectionFactory db, HttpContext ctx, CancellationToken ct) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");
            if (userId is null || !Guid.TryParse(userId, out var userGuid))
                return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM public.faceit_accounts WHERE user_id = @userId", new { userId = userGuid });
            await conn.ExecuteAsync(
                "UPDATE public.profiles SET faceit_nickname = NULL WHERE id = @userId",
                new { userId = userGuid });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // =====================================================================
        //  PROXY endpoints (unchanged)
        // =====================================================================

        // ── POST /api/integrations/riot/proxy ─────────────────────────────────
        app.MapPost("/api/integrations/riot/proxy", async (
            [FromBody] RiotProxyRequest req,
            RiotApiClient               riot,
            CancellationToken           ct) =>
        {
            var (status, body) = await riot.ProxyAsync(req.Region, req.Endpoint, ct);
            return Results.Content(body, "application/json", statusCode: status);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/integrations/faceit/proxy ───────────────────────────────
        app.MapPost("/api/integrations/faceit/proxy", async (
            [FromBody] FaceitProxyRequest req,
            FaceitApiClient               faceit,
            CancellationToken             ct) =>
        {
            var (status, body) = await faceit.ProxyAsync(req.Endpoint, ct);
            return Results.Content(body, "application/json", statusCode: status);
        }).RequireAuthorization("Authenticated");
    }
}
