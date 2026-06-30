using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Esportra.Api.Services;
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
            HttpContext ctx,
            IConfiguration config,
            OAuthStateProtector stateProtector) =>
        {
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");
            if (userId is null) return Results.Unauthorized();

            var clientId = config["Riot:OAuthClientId"] ?? string.Empty;
            var backendUrl = ResolveBackendUrl(config);
            var redirectUri = $"{backendUrl}/api/integrations/riot/callback";

            var state = stateProtector.Protect(new OAuthStatePayload(
                UserId: userId,
                Provider: "riot",
                CodeVerifier: null,
                CreatedAt: DateTime.UtcNow));

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
            HttpContext ctx,
            IConfiguration config,
            OAuthStateProtector stateProtector,
            IDbConnectionFactory db,
            HttpClient http,
            CancellationToken ct) =>
        {
            var frontendUrl = ResolveFrontendUrl(config);
            var code = ctx.Request.Query["code"].FirstOrDefault();
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

            var clientId = config["Riot:OAuthClientId"] ?? string.Empty;
            var clientSecret = config["Riot:OAuthClientSecret"] ?? string.Empty;
            var backendUrl = ResolveBackendUrl(config);
            var redirectUri = $"{backendUrl}/api/integrations/riot/callback";

            // Exchange code for tokens
            var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://auth.riotgames.com/token");
            tokenReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            });

            var tokenRes = await http.SendAsync(tokenReq, ct);
            if (!tokenRes.IsSuccessStatusCode)
                return Results.Redirect($"{frontendUrl}/account/settings?riot_linked=error&reason=token_exchange_failed");

            var tokenBody = await tokenRes.Content.ReadAsStringAsync(ct);
            using var tokenDoc = JsonDocument.Parse(tokenBody);
            var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()!;
            var refreshToken = tokenDoc.RootElement.TryGetProperty("refresh_token", out var rt)
                ? rt.GetString() : null;
            var expiresIn = tokenDoc.RootElement.TryGetProperty("expires_in", out var ei)
                ? ei.GetInt32() : 3600;
            var expiresAt = DateTime.UtcNow.AddSeconds(expiresIn);

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
            var puuid = infoDoc.RootElement.GetProperty("puuid").GetString()!;
            var gameName = infoDoc.RootElement.GetProperty("gameName").GetString()!;
            var tagLine = infoDoc.RootElement.GetProperty("tagLine").GetString()!;

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

            // Sync profiles.riot_tag (reject if already linked to another account)
            var riotTag = $"{gameName}#{tagLine}";
            var existingOwner = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM public.profiles WHERE riot_tag = @riotTag AND id != @userId",
                new { riotTag, userId = userGuid });
            if (existingOwner is not null)
                return Results.Redirect($"{frontendUrl}/account/settings?riot_linked=error&reason=already_linked");

            await conn.ExecuteAsync(
                "UPDATE public.profiles SET riot_tag = @riotTag WHERE id = @userId",
                new { riotTag, userId = userGuid });

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
                linked = true,
                puuid = (string?)account.puuid,
                game_name = (string?)account.game_name,
                tag_line = (string?)account.tag_line,
                region = (string?)account.region,
                riot_tag = (string?)account.riot_tag
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
        //  PROXY endpoints
        // =====================================================================

        // ── POST /api/integrations/riot/proxy ─────────────────────────────────
        app.MapPost("/api/integrations/riot/proxy", async (
            [FromBody] RiotProxyRequest req,
            RiotApiClient riot,
            CancellationToken ct) =>
        {
            var (status, body) = await riot.ProxyAsync(req.Region, req.Endpoint, ct);
            return Results.Content(body, "application/json", statusCode: status);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/integrations/riot/enriched-match ────────────────────────
        // Fetches a Valorant match from Riot and attaches parsed analytics fields.
        app.MapPost("/api/integrations/riot/enriched-match", async (
            [FromBody] RiotEnrichedMatchRequest req,
            RiotApiClient riot,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.MatchId))
            {
                return Results.BadRequest(new { error = "matchId is required" });
            }

            var (status, body) = await riot.ProxyAsync(
                req.Region,
                $"/val/match/v1/matches/{req.MatchId}",
                ct);

            if (status != 200)
            {
                return Results.Content(body, "application/json", statusCode: status);
            }

            using var doc = JsonDocument.Parse(body);
            var emptyTeams = new HashSet<string>(StringComparer.Ordinal);
            var parsed = RiotMatchDetailsParser.Parse(doc.RootElement, emptyTeams, emptyTeams);
            if (parsed is null)
            {
                return Results.BadRequest(new { error = "Failed to parse Riot match payload" });
            }

            var serializedDerived = ValorantMatchStatsHelper.SerializeDerivedDetails(parsed.Derived);
            var root = JsonNode.Parse(body)?.AsObject();
            if (root is null)
            {
                return Results.BadRequest(new { error = "Invalid Riot match payload" });
            }

            root["enrichedPlayers"] = JsonSerializer.SerializeToNode(parsed.Players);
            root["matchInfoParsed"] = JsonSerializer.SerializeToNode(parsed.MatchInfo);
            root["roundTimeline"] = JsonSerializer.SerializeToNode(serializedDerived.RoundTimeline);
            root["economyTimeline"] = JsonSerializer.SerializeToNode(serializedDerived.EconomyTimeline);
            root["weaponSummaries"] = JsonSerializer.SerializeToNode(serializedDerived.WeaponSummaries);

            return Results.Json(root);
        }).RequireAuthorization("Authenticated");
    }
}
